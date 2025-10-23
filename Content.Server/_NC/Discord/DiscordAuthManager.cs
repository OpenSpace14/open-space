using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._NC.CCCvars;
using Content.Shared._NC.DiscordAuth;
using Lidgren.Network;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Server._NC.Discord;

public sealed class DiscordUserData
{
    public string UserId { get; set; } = default!;
    public string DiscordId { get; set; } = default!;
    public int SponsorLevel { get; set; }
}

public sealed class DiscordLinkResponse
{
    public string Link { get; set; } = default!;
}

public sealed class MsgDiscordAuthSuccess : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
    }
}

public sealed class DiscordAuthManager : IPostInjectInit
{
    [Dependency] private INetManager _net = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IConfigurationManager _configuration = default!;
    private ISawmill _sawmill = default!;
    private string _apiUrl = default!;
    private string _apiKey = default!;
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<NetUserId, DiscordUserData> _cachedDiscordUsers = new();

    public event EventHandler<ICommonSession>? PlayerVerified;

    public void PostInject()
    {
        IoCManager.InjectDependencies(this);
    }

    public void Initialize()
    {
        _configuration.OnValueChanged(CCCVars.DiscordApiUrl, (value) => _apiUrl = value, true);
        _configuration.OnValueChanged(CCCVars.ApiKey, (value) => _apiKey = value, true);
        _sawmill = Logger.GetSawmill("discord_auth");
        _net.RegisterNetMessage<MsgDiscordAuthRequired>();
        _net.RegisterNetMessage<MsgDiscordAuthCheck>(OnAuthCheck);
        _net.RegisterNetMessage<MsgDiscordAuthSuccess>();
        _net.Disconnect += OnDisconnect;
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
        PlayerVerified += OnPlayerVerified;
    }

    private void OnPlayerVerified(object? sender, ICommonSession e)
    {
        _sawmill.Debug($"DiscordAuth: Player {e.Name} verified, sending to game");

        // Отправляем сообщение об успешной авторизации клиенту
        e.Channel.SendMessage(new MsgDiscordAuthSuccess());

        Timer.Spawn(0, () => _playerManager.JoinGame(e));
    }

    private void OnDisconnect(object? sender, NetDisconnectedArgs e)
    {
        _cachedDiscordUsers.Remove(e.Channel.UserId);
    }

    private async void OnAuthCheck(MsgDiscordAuthCheck msg)
    {
        _sawmill.Debug($"DiscordAuth: Received auth check from {msg.MsgChannel.UserId}");

        var data = await IsVerified(msg.MsgChannel.UserId);
        if (data is null)
        {
            _sawmill.Debug($"DiscordAuth: User {msg.MsgChannel.UserId} not verified in auth check");
            return;
        }

        var session = _playerManager.GetSessionById(msg.MsgChannel.UserId);
        _cachedDiscordUsers.TryAdd(session.UserId, data);

        // Отправляем сообщение об успешной авторизации клиенту
        session.Channel.SendMessage(new MsgDiscordAuthSuccess());

        PlayerVerified?.Invoke(this, session);
    }

    private async void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus != SessionStatus.Connected)
            return;

        _sawmill.Debug($"DiscordAuth: Checking Discord verification for user {args.Session.UserId} ({args.Session.Name})");

        try
        {
            var data = await IsVerified(args.Session.UserId);
            if (data is not null)
            {
                _sawmill.Debug($"DiscordAuth: User {args.Session.UserId} is verified, notifying systems");
                _cachedDiscordUsers.TryAdd(args.Session.UserId, data);

                // Отправляем сообщение об успешной авторизации клиенту
                args.Session.Channel.SendMessage(new MsgDiscordAuthSuccess());

                PlayerVerified?.Invoke(this, args.Session);
                return;
            }

            _sawmill.Debug($"DiscordAuth: User {args.Session.UserId} is not verified, sending to Discord auth");

            // Генерируем ссылку напрямую, не вызывая IsVerified повторно
            var requestUrl = $"{_apiUrl}/link?userid={args.Session.UserId}&api_token={_apiKey}";
            var response = await _httpClient.GetAsync(requestUrl);

            if (response.IsSuccessStatusCode)
            {
                var linkResponse = await response.Content.ReadFromJsonAsync<DiscordLinkResponse>();
                var message = new MsgDiscordAuthRequired() { Link = linkResponse!.Link };
                args.Session.Channel.SendMessage(message);
                _sawmill.Debug($"DiscordAuth: Sent Discord auth link to user {args.Session.UserId}");

                // НЕ вызываем PlayerVerified - игрок должен остаться в состоянии авторизации
                // JoinQueueManager будет ждать, пока игрок не пройдёт верификацию
            }
            else
            {
                _sawmill.Error($"DiscordAuth: Failed to generate Discord auth link for user {args.Session.UserId}. Status: {response.StatusCode}");

                // Даже при ошибке отправляем стандартную ссылку
                var fallbackMessage = new MsgDiscordAuthRequired() { Link = "https://discord.gg/cncwdkTWRK" };
                args.Session.Channel.SendMessage(fallbackMessage);
                _sawmill.Debug($"DiscordAuth: Sent fallback Discord auth link to user {args.Session.UserId}");

                // НЕ вызываем PlayerVerified - игрок должен остаться в состоянии авторизации
            }
        }
        catch (Exception ex)
        {
            _sawmill.Error($"DiscordAuth: Error in OnPlayerStatusChanged for user {args.Session.UserId}: {ex.Message}");

            // При любой ошибке отправляем стандартную ссылку
            var fallbackMessage = new MsgDiscordAuthRequired() { Link = "https://discord.gg/cncwdkTWRK" };
            args.Session.Channel.SendMessage(fallbackMessage);
            _sawmill.Debug($"DiscordAuth: Sent fallback Discord auth link after error to user {args.Session.UserId}");

            // НЕ вызываем PlayerVerified - игрок должен остаться в состоянии авторизации
        }
    }

    public async Task<DiscordUserData?> IsVerified(NetUserId userId, CancellationToken cancel = default)
    {
        _sawmill.Debug($"DiscordAuth: Checking verification for user {userId}");
        var requestUrl = $"{_apiUrl}/check?userid={userId}&api_token={_apiKey}";

        try
        {
            var response = await _httpClient.GetAsync(requestUrl, cancel);

            _sawmill.Debug($"DiscordAuth: Response status for {userId}: {response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Пользователь не найден - это нормально, значит не привязан
                _sawmill.Debug($"DiscordAuth: User {userId} not found in Discord links");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _sawmill.Error($"DiscordAuth: Failed to check verification for {userId}: {response.StatusCode}, content: {errorContent}");
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _sawmill.Debug($"DiscordAuth: Response content for {userId}: {responseContent}");

            try
            {
                var discordData = await response.Content.ReadFromJsonAsync<DiscordUserData>(cancel);
                if (discordData != null)
                {
                    _sawmill.Debug($"DiscordAuth: User {userId} is verified - DiscordId: {discordData.DiscordId}, SponsorLevel: {discordData.SponsorLevel}");
                }
                return discordData;
            }
            catch (Exception ex)
            {
                _sawmill.Error($"DiscordAuth: Failed to parse JSON for user {userId}: {ex.Message}, content: {responseContent}");
                return null;
            }
        }
        catch (Exception ex)
        {
            _sawmill.Error($"DiscordAuth: Exception in IsVerified for user {userId}: {ex.Message}");
            return null;
        }
    }

    public async Task<string> GenerateLink(NetUserId userId, CancellationToken cancel = default)
    {
        _sawmill.Debug($"DiscordAuth: Generating link for {userId}");
        var requestUrl = $"{_apiUrl}/link?userid={userId}&api_token={_apiKey}";

        try
        {
            var response = await _httpClient.GetAsync(requestUrl, cancel);

            _sawmill.Debug($"DiscordAuth: Link response status for {userId}: {response.StatusCode}");
            var responseContent = await response.Content.ReadAsStringAsync();
            _sawmill.Debug($"DiscordAuth: Link response content for {userId}: {responseContent}");

            if (!response.IsSuccessStatusCode)
            {
                _sawmill.Error($"DiscordAuth: Failed to generate link for {userId}: {response.StatusCode}, content: {responseContent}");
                return "https://discord.gg/cncwdkTWRK"; // Возвращаем fallback ссылку
            }

            try
            {
                var link = await response.Content.ReadFromJsonAsync<DiscordLinkResponse>(cancel);
                _sawmill.Debug($"DiscordAuth: Generated link for {userId}: {link!.Link}");
                return link!.Link;
            }
            catch (Exception ex)
            {
                _sawmill.Error($"DiscordAuth: Failed to parse link JSON for {userId}: {ex.Message}, content: {responseContent}");
                return "https://discord.gg/cncwdkTWRK"; // Возвращаем fallback ссылку
            }
        }
        catch (Exception ex)
        {
            _sawmill.Error($"DiscordAuth: Exception in GenerateLink for {userId}: {ex.Message}");
            return "https://discord.gg/cncwdkTWRK"; // Возвращаем fallback ссылку
        }
    }
}

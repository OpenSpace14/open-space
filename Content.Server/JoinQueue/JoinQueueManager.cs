using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Connection;
using Content.Server._NC.Discord;
using Content.Shared.CCVar;
using Content.Shared.JoinQueue;
using Prometheus;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.JoinQueue;

public sealed class JoinQueueManager
{
    private static readonly Gauge QueueCount = Metrics.CreateGauge(
        "join_queue_count",
        "Amount of players in queue.");

    private static readonly Counter QueueBypassCount = Metrics.CreateCounter(
        "join_queue_bypass_count",
        "Amount of players who bypassed queue by privileges.");

    private static readonly Histogram QueueTimings = Metrics.CreateHistogram(
        "join_queue_timings",
        "Timings of players in queue",
        new HistogramConfiguration()
        {
            LabelNames = new[] {"type"},
            Buckets = Histogram.ExponentialBuckets(1, 2, 14),
        });

    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IConnectionManager _connection = default!;
    [Dependency] private readonly IConfigurationManager _configuration = default!;
    [Dependency] private readonly IServerNetManager _net = default!;
    [Dependency] private readonly DiscordAuthManager _discordAuth = default!;

    private ISawmill _sawmill = default!;
    private readonly List<ICommonSession> _queue = new();
    private bool _isEnabled = false;

    public int PlayerInQueueCount => _queue.Count;
    public int ActualPlayersCount => _player.PlayerCount - PlayerInQueueCount;

    public void Initialize()
    {
        _sawmill = Logger.GetSawmill("join_queue");
        _net.RegisterNetMessage<QueueUpdateMessage>();
        _configuration.OnValueChanged(CCVars.QueueEnabled, OnQueueCVarChanged, true);
        _player.PlayerStatusChanged += OnPlayerStatusChanged;

        // _discordAuth.PlayerVerified += OnPlayerVerified;
    }

    private void OnQueueCVarChanged(bool value)
    {
        _isEnabled = value;
        _sawmill.Info($"Queue enabled: {value}");

        if (!value)
        {
            foreach (var session in _queue)
            {
                session.Channel.Disconnect("Queue was disabled");
            }
            _queue.Clear();
        }
    }

    private async void OnPlayerVerified(object? sender, ICommonSession session)
    {
        _sawmill.Debug($"JoinQueue: Player {session.Name} verified, checking if should send to game or queue");

        if (!_isEnabled)
        {
            _sawmill.Debug($"JoinQueue: Queue disabled, sending {session.Name} directly to game");
            SendToGame(session);
            return;
        }

        // Проверяем, прошёл ли игрок Discord авторизацию
        var discordData = await _discordAuth.IsVerified(session.UserId);
        if (discordData == null)
        {
            _sawmill.Debug($"JoinQueue: Player {session.Name} not Discord verified, keeping in auth state");
            return; // Не отправляем в игру, игрок остаётся в состоянии Discord авторизации
        }

        var isPrivileged = false;
        var currentOnline = _player.PlayerCount - 1;
        var haveFreeSlot = currentOnline < _configuration.GetCVar(CCVars.SoftMaxPlayers);

        if (isPrivileged || haveFreeSlot)
        {
            _sawmill.Debug($"JoinQueue: Sending {session.Name} to game (privileged: {isPrivileged}, free slot: {haveFreeSlot})");
            SendToGame(session);
            if (isPrivileged && !haveFreeSlot)
                QueueBypassCount.Inc();
            return;
        }

        _sawmill.Debug($"JoinQueue: Adding {session.Name} to queue");
        _queue.Add(session);
        ProcessQueue(false, session.ConnectedTime);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus == SessionStatus.Disconnected)
        {
            var wasInQueue = _queue.Remove(e.Session);
            if (!wasInQueue && e.OldStatus != SessionStatus.InGame)
                return;
            ProcessQueue(true, e.Session.ConnectedTime);
            if (wasInQueue)
                QueueTimings.WithLabels("Unwaited").Observe((DateTime.UtcNow - e.Session.ConnectedTime).TotalSeconds);
        }
    }

    private void ProcessQueue(bool isDisconnect, DateTime connectedTime)
    {
        var players = ActualPlayersCount;
        if (isDisconnect)
            players--;

        var haveFreeSlot = players < _configuration.GetCVar(CCVars.SoftMaxPlayers);
        var queueContains = _queue.Count > 0;

        if (haveFreeSlot && queueContains)
        {
            var session = _queue.First();
            _queue.Remove(session);
            SendToGame(session);
            QueueTimings.WithLabels("Waited").Observe((DateTime.UtcNow - connectedTime).TotalSeconds);
        }

        SendUpdateMessages();
        QueueCount.Set(_queue.Count);
    }

    private void SendUpdateMessages()
    {
        for (var i = 0; i < _queue.Count; i++)
        {
            _queue[i].Channel.SendMessage(new QueueUpdateMessage
            {
                Total = _queue.Count,
                Position = i + 1,
            });
        }
    }

    private void SendToGame(ICommonSession session)
    {
        Timer.Spawn(0, () => _player.JoinGame(session));
    }
}

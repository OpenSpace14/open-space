import requests
import argparse
import configparser
from pathlib import Path

API_URL = "https://ntts.fdev.team/api/v1/tts/speakers"
BEARER_TOKEN = "24d25cc0789d68692ebfdf1bd24b1733baadc89f" # https://discord.gg/TjGAMFwWt6 (/me)

def fetch_speakers():
    """Получает список спикеров из API"""
    headers = {"Authorization": f"Bearer {BEARER_TOKEN}"}
    response = requests.get(API_URL, headers=headers)
    response.raise_for_status()
    return response.json()

def generate_empty_ini(data):
    """Генерирует пустой icons.ini с источниками"""
    sources = set()
    for voice in data['voices']:
        sources.add(voice['source'])
    
    config = configparser.ConfigParser()
    config['Icons'] = {}
    
    for source in sorted(sources):
        config['Icons'][source] = ''
    
    with open('icons.ini', 'w', encoding='utf-8') as f:
        config.write(f)
    
    print(f"✓ Создан icons.ini с {len(sources)} источниками (пустой)")

def load_icons():
    """Загружает иконки из icons.ini"""
    config = configparser.ConfigParser()
    if Path('icons.ini').exists():
        config.read('icons.ini', encoding='utf-8')
        if 'Icons' in config:
            return {k.lower(): v.strip() for k, v in config['Icons'].items()}
    return {}

def generate_yml(data, icons):
    """Генерирует tts-voices.yml"""
    with open('tts-voices.yml', 'w', encoding='utf-8') as f:
        for voice in data['voices']:
            speaker_id = voice['speakers'][0]
            # Преобразуем speaker_id в CamelCase для id
            voice_id = ''.join(word.capitalize() for word in speaker_id.replace('_', ' ').replace('-', ' ').split())
            sex = 'Male' if voice['gender'] == 'male' else 'Female'
            
            f.write(f"- type: ttsVoice\n")
            f.write(f"  id: {voice_id}\n")
            f.write(f"  name: tts-{speaker_id.replace('_', '-')}\n")
            f.write(f"  sex: {sex}\n")
            f.write(f"  speaker: {speaker_id}\n")
            f.write(f"  roundStart: true\n")
            f.write(f"\n")
    
    print(f"✓ Создан tts-voices.yml с {len(data['voices'])} голосами")

def generate_ftl(data, icons):
    """Генерирует tts-voices.ftl"""
    with open('tts-voices.ftl', 'w', encoding='utf-8') as f:
        for voice in data['voices']:
            speaker_id = voice['speakers'][0]
            name = voice['name']
            source = voice['source'].lower()
            icon = icons.get(source, '').strip()
            
            ftl_key = f"tts-{speaker_id.replace('_', '-')}"
            
            # Если есть иконка - добавляем в скобках, если нет - просто имя
            if icon:
                ftl_value = f"{name} ( {icon} )"
            else:
                ftl_value = name
            
            f.write(f"{ftl_key} = {ftl_value}\n")
    
    print(f"✓ Создан tts-voices.ftl с {len(data['voices'])} голосами")

def main():
    parser = argparse.ArgumentParser(description='Генератор конфигов для TTS голосов')
    parser.add_argument('--genini', action='store_true', help='Создать пустой icons.ini')
    parser.add_argument('--all', action='store_true', help='Создать все файлы (yml + ftl)')
    
    args = parser.parse_args()
    
    if not args.genini and not args.all:
        parser.print_help()
        return
    
    print("Получение данных из API...")
    data = fetch_speakers()
    print(f"Получено {len(data['voices'])} голосов")
    
    if args.genini:
        generate_empty_ini(data)
    
    if args.all:
        print("\nЗагрузка иконок из icons.ini...")
        icons = load_icons()
        if icons:
            filled = sum(1 for v in icons.values() if v)
            print(f"Загружено {len(icons)} источников, заполнено иконок: {filled}")
        else:
            print("⚠ icons.ini не найден или пуст. Иконки не будут использованы.")
        
        print("\nГенерация файлов...")
        generate_yml(data, icons)
        generate_ftl(data, icons)
    
    print("\n✓ Готово!")

if __name__ == "__main__":
    main()

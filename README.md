# Инструкция по запуску

## Требования

- Windows
- .NET Framework 4.8 или выше
- Visual Studio (для сборки из исходников) или готовый исполняемый файл

## Установка

1. Клонируйте репозиторий:
   ```bash
   git clone https://github.com/suppro/io.git
   cd io
   ```

2. Скачайте данные Tesseract OCR:
   - Перейдите на официальный сайт: https://github.com/tesseract-ocr/tessdata
   - Скачайте папку `tessdata` или необходимые файлы
   - Поместите папку `tessdata` в директорию `io/bin/Debug/`
   
   Или используйте прямую ссылку для скачивания:
   ```
   https://github.com/tesseract-ocr/tessdata/archive/refs/heads/main.zip
   ```
   Распакуйте архив и переименуйте папку `tessdata-main` в `tessdata`, затем переместите в `io/bin/Debug/`

3. Подготовьте файлы роутов:
   - Убедитесь, что в папке `io/bin/Debug/` находятся следующие файлы:
     - `route.txt` - маршрут по кликам
     - `wasd_route.txt` - WASD маршрут
     - `enter_route.txt` - маршрут захода в рейд
     - `img.png` - изображение для проверки (опционально)

## Запуск

### Вариант 1: Запуск из Visual Studio

1. Откройте файл `io.slnx` в Visual Studio
2. Нажмите F5 или выберите "Start Debugging"
3. Программа запустится

### Вариант 2: Запуск готового исполняемого файла

1. Перейдите в папку `io/bin/Debug/`
2. Запустите `io.exe`
3. Программа запустится

## Первый запуск

При первом запуске убедитесь, что:
- Все необходимые файлы находятся в папке `io/bin/Debug/`
- Папка `tessdata` находится в `io/bin/Debug/tessdata/`
- Игра "World" запущена (окно с названием "World" должно быть открыто)


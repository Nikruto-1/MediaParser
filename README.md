# MediaParser

Локальний ASP.NET Core MVC застосунок для перевірки публічних профілів за нікнеймом або ПІБ, керування джерелами та експорту результатів у Excel. Sherlock не використовується.

## Запуск

Потрібні [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) і Google Chrome для перевірок вебсторінок.

```powershell
dotnet restore MediaParser.sln
dotnet run --project MediaParser/MediaParser.csproj --launch-profile http
```

Відкрийте http://localhost:5125. Локальна SQLite-база створюється автоматично у `MediaParser/App_Data/mediaparser.db` з 19 джерелами з попереднього списку. Наявні записи при повторному запуску не перезаписуються. Оберіть джерела в меню «Обрати медії», введіть нікнейм і запустіть пошук.

Selenium Manager автоматично підбирає ChromeDriver до встановленого Chrome; перший вебпошук потребує доступу до інтернету для драйвера. Кеш драйверів зберігається в локальній `.cache/selenium`. Перевірки працюють у headless Chrome з окремим тимчасовим профілем для кожного пошуку, без особистих cookie Chrome.

## Налаштування

`appsettings.json` містить загальні налаштування без секретів. Приватні параметри можна задати через змінні середовища або локальний `MediaParser/appsettings.Local.json`, виключений із Git та publish.

SQLite використовується за замовчуванням. Для наявної SQL Server бази встановіть `Database__Provider=SqlServer` і `ConnectionStrings__DefaultConnection` або відповідні значення у локальному JSON. Програма не створює і не змінює схему SQL Server автоматично. Колонка `ApiKey` для SQL Server залишається `VARBINARY(MAX)`.

Нові API-ключі шифруються ASP.NET Core Data Protection. Файли ключів у `App_Data/keys` потрібні для подальшого розшифрування: зберігайте їх разом із локальною базою. На Windows вони захищені DPAPI. Старі AES-ключі SQL Server читаються за наявності оригінальних `Encryption:Key` і `Encryption:IV` у приватній конфігурації.

Джерела редагуються через «Редагувати медії». URL-шаблон має містити `{0}`; ErrorKeywords — JSON-масив рядків. Порожнє поле API-ключа під час редагування зберігає наявний ключ.

## Перевірки

```powershell
dotnet build MediaParser.sln --no-restore --warnaserror
dotnet run --project tests/algorithm/AlgorithmRegressionTests.csproj
dotnet run --project tests/media/MediaParser.MediaTests.csproj
dotnet run --project tests/storage/MediaParser.StorageTests.csproj
dotnet list MediaParser/MediaParser.csproj package --vulnerable --include-transitive
```

Додаткова перевірка справжнього Chrome на локальних тестових сторінках:

```powershell
dotnet run --project tests/algorithm/AlgorithmRegressionTests.csproj -- --browser
```

Для HTTP-перевірки запущеного локального сайту: `python tests/http/smoke.py` (Python 3, без додаткових пакетів). Перевірка створює та видаляє лише власне тестове джерело.

Регресійні перевірки без `--browser` не звертаються до соціальних мереж. Не запускайте build паралельно із `dotnet run` на Windows, де executable може бути заблокований процесом сервера.

## Межі результатів

Соцмережі можуть показувати авторизацію, капчу, блокування або змінювати розмітку. У таких випадках програма показує обмеження чи непідтверджений результат. Збіг нікнейму не встановлює особу власника профілю. Пошук за ПІБ повертає сторінку результатів для ручної перевірки. Початкові URL та правила 19 джерел потребують перевірки відповідно до поточного стану кожного сервісу.

Застосунок призначений для локального використання: керування джерелами не має автентифікації. HTTP-профіль слухає лише localhost. Перед розгортанням у мережі потрібні автентифікація, розмежування доступу та обмеження URL-запитів.

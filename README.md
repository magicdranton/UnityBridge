# UnityBridge — этап 1

Консольное приложение .NET 10 с Generic Host, Kestrel и последовательной очередью компиляции Unity. Внешние NuGet-пакеты не нужны: используется Microsoft.AspNetCore.App.

## Запуск на Windows

Установите .NET 10 SDK. Проверьте `Unity.EditorPath`, `Unity.ProjectPath` и `Unity.LogDirectory` в `appsettings.json`. Относительный каталог логов разрешается от рабочего каталога. В PowerShell из каталога UnityBridge:

```powershell
$env:Bridge__ApiToken = 'replace-with-a-long-random-token'
dotnet run
```

`CHANGE_ME` намеренно блокирует запуск. Настройки можно переопределять переменными окружения с двойным подчёркиванием. При отсутствии Editor или проекта сервер продолжает работать, `/health` показывает доступность, 
а запуск задания возвращает 503. Проект считается доступным при наличии Assets и ProjectSettings.

## HTTP API

- `GET /health` — без авторизации, JSON с доступностью Unity и проекта и текущим заданием.
- `GET /api/v1/status` — версии Bridge и Unity, доступность, текущее задание и счётчики очереди.
- `POST /api/v1/jobs/compile` — тело `{}`, ответ 202 с jobId; 429 при заполнении очереди.
- `GET /api/v1/jobs/{id}` — состояние и результат компиляции.
- `GET /api/v1/jobs/{id}/logs?lines=100` — последние строки, максимум 300 (или MaxLogLines).

Для всех запросов, кроме `/health`, передавайте `Authorization: Bearer <token>`.

В `/api/v1/status` `completedJobs` учитывает все завершённые задания (включая ошибки, отмены и тайм-ауты), `failedJobs` — задания Failed и TimedOut. Счётчики сбрасываются при перезапуске. `unityVersion` читается из метаданных настроенного Editor; если версия недоступна, возвращается `null`.

```powershell
$headers = @{ Authorization = "Bearer $env:Bridge__ApiToken" }
Invoke-RestMethod http://localhost:8765/health
$job = Invoke-RestMethod http://localhost:8765/api/v1/jobs/compile -Method Post -Headers $headers -ContentType application/json -Body '{}'
Invoke-RestMethod "http://localhost:8765$($job.statusUrl)" -Headers $headers
Invoke-RestMethod "http://localhost:8765$($job.logsUrl)" -Headers $headers
```

Из Docker используйте `http://host.docker.internal:8765`. На Windows ограничьте доступ к порту правилами Firewall. Bridge не предоставляет API для изменения путей или аргументов процесса.

## Границы этапа

Очередь в памяти, не более трёх ожидающих заданий, один Unity-процесс. Успех требует нулевого exit code, наличия лога и отсутствия известных признаков ошибок. Наличие DLL не используется как доказательство компиляции. Тайм-аут и остановка Bridge завершают дерево процесса. Логи Unity сохраняются отдельно по GUID, диагностические события Bridge идут через ILogger в консоль.

Тесты Unity, сборка, отдельный endpoint отмены, история заданий и очистка логов относятся к следующим этапам. Логи пока следует удалять вручную при остановленном Bridge. Доступ к одному проекту из нескольких экземпляров Bridge не координируется: запускайте один экземпляр и закрывайте интерактивный Editor перед batch-компиляцией.

Реальную компиляцию HeliWords и доступ из Docker нужно подтвердить на Windows с установленным Editor и лицензией.

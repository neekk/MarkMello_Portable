# M0 — Naiad spike результат

## Статус

Закрыт зелёным. Mermaid source успешно рендерится в SVG через Naiad внутри .NET-процесса. Решение по продолжению ADR-0005: **продолжать**, с одновременным выполнением M0.5 (runtime upgrade на `net10.0`, см. ADR-0005 Decision 10).

## Дата

2026-05-17

## Что проверено

### Naiad NuGet-пакет

- ID: `Naiad`, владелец `simoncropp` (репо в 0.1.2 уже переехал на `github.com/NaiadDiagrams/Naiad`).
- Лицензия: MIT.
- Версии в фиде: `0.1.0`, `0.1.1`, `0.1.2`. Последняя стабильная — **`0.1.2`**.
- Единственная транзитивная зависимость: `Pidgin 3.5.1` (parser combinator, чистый managed .NET).

### Состав пакета

В `lib/` присутствует только `net10.0/Naiad.dll` + xmldoc. Других TFM (`net9.0`, `net8.0`, `netstandard2.x`) **нет**.

```text
naiad/0.1.2/lib/net10.0/Naiad.dll   1 061 376 bytes
naiad/0.1.2/lib/net10.0/Naiad.xml       7 998 bytes
```

Нативных бинарей, payload-ов, сопровождающих скриптов нет — один managed DLL.

### Публичный API

Точка входа фактически называется `MermaidSharp.Mermaid`, а не `Naiad.Mermaid`. README пакета по этому пункту вводит в заблуждение.

```csharp
namespace MermaidSharp;

public static class Mermaid
{
    public static string Render(string input, RenderOptions options);
    public static DiagramType DetectDiagramType(string input);
}
```

- Перегрузки `Render(string)` без `RenderOptions` **в публичном API нет**, хотя README показывает именно её. При интеграции `MermaidDiagramRenderer` обязан всегда передавать `RenderOptions` (тип публичный, в этом же namespace).
- Метод синхронный, возвращает `string` с SVG.
- Для ошибок объявлены `MermaidSharp.MermaidException` и `MermaidSharp.MermaidParseException` — это естественные точки для конвертации в `DiagramRenderFailure`.
- Покрытые диаграммы (по namespaces в `MermaidSharp.Diagrams.*`): Architecture, Block, C4, Class, ER, Flowchart, Gantt, Sequence, State, Timeline, Treemap, UserJourney, XYChart, Pie (через `MermaidStyles.PieStyles`). Перекрывает все запрошенные планом сценарии (flowchart, sequence, class/state).

### Зависимости и побочные стеки

Прямые ассембли-references у `Naiad.dll`:

```text
Pidgin 3.5.1.0
System.Collections 10.0.0.0
System.Linq 10.0.0.0
System.Memory 10.0.0.0
System.Runtime 10.0.0.0
System.Text.RegularExpressions 10.0.0.0
```

Скан строк бинаря на маркеры внешних рантаймов:

| Маркер | Найдено | Комментарий |
| --- | --- | --- |
| `puppeteer` | 0 | — |
| `chromium` | 0 | — |
| `webview` | 0 | — |
| `npm` | 0 | — |
| `mermaid.ink` | 0 | — |
| `Process.Start` | 0 | — |
| `System.Net.Http` | 0 | — |
| `System.Diagnostics.Process` | 0 | — |
| `http://` / `https://` | 1 | единственное совпадение — `https://github.com/NaiadDiagrams/Naiad` в метаданных пакета |
| `node` | 114 | все совпадения — `MermaidSharp.Models.Node` и производные имена (граф-нода Mermaid), не Node.js |
| `cli` | 2 | внутри `clickDirective` (Mermaid click action), не CLI |

Вывод по конституции: **WebView/Node/Chromium/Puppeteer/Mermaid CLI/сетевой рендер/внешний процесс отсутствуют** в самом рендерере. Naiad — чистая managed .NET-библиотека, in-process.

## Результат запуска

В spike-проекте `spike/NaiadSpike/` (gitignored, не входит в solution) на `net10.0` с `PackageReference Naiad 0.1.2` выполнен реальный вызов:

```csharp
const string source = """
    flowchart LR
        A[Start] --> B[Process] --> C[End]
    """;

var detected = MermaidSharp.Mermaid.DetectDiagramType(source);
var svg = MermaidSharp.Mermaid.Render(source, new MermaidSharp.RenderOptions());
```

Output:

```text
Detected diagram type: Flowchart
SVG length: 7609 chars
Starts with <svg: True
Ends with </svg>: True
Written: spike/NaiadSpike/bin/Debug/net10.0/flowchart.svg
```

Невалидный source:

```csharp
MermaidSharp.Mermaid.Render("this is not mermaid", new RenderOptions());
```

бросает:

```text
Exception type: MermaidSharp.MermaidException
Message: Unknown diagram type in: this is not mermaid
```

— ровно тот controlled failure, который ADR-0005 ожидает от `MermaidDiagramRenderer`.

## Наблюдения по фактическому SVG-output (вход в M5)

Голова сгенерированного SVG:

```xml
<svg id="mermaid-svg" width="100%" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 385.5 88"
     style="max-width: 385.5px;" role="graphics-document document"
     xmlns:xlink="http://www.w3.org/1999/xlink">
  <style xmlns="http://www.w3.org/1999/xhtml">
    @import url("https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.7.2/css/all.min.css");
  </style>
  <style>#mermaid-svg{font-family:"trebuchet ms",...}@keyframes ...</style>
  ...
</svg>
```

Хвост:

```xml
<foreignObject ...>
  <div xmlns="http://www.w3.org/1999/xhtml" style="display: table-cell; ...">
    <span class="nodeLabel"><p>End</p></span>
  </div>
</foreignObject>
</svg>
```

Конкретные блокеры/риски для M5 (`AotSafeSvgImage` extension):

- `<foreignObject>` с вложенным HTML (`<div>/<span>/<p>` в `xmlns="http://www.w3.org/1999/xhtml"`) для текстовых лейблов узлов — текущий supported subset `AotSafeSvgImage` это не покрывает.
- Множественные `<style>` блоки с CSS animations (`@keyframes`), CSS-селекторами (`#mermaid-svg .edge-animation-slow{...}`), `!important`.
- `@import url("https://cdnjs.cloudflare.com/...")` для FontAwesome иконок. Это не сетевой запрос со стороны Naiad (рендерер уже завершил работу), но если pipeline когда-либо будет резолвить CSS @imports, мы получим внешний network egress. Для viewer-mode native renderer этот @import нужно либо игнорировать (вероятно так и будет — наш SVG-path его не парсит), либо явно вырезать из выхода перед отображением. Решение фиксируется в M5 (SVG compatibility) и/или M7 (acknowledgement, если оставляем FontAwesome attribution).
- `xmlns:xlink` объявление присутствует, но в наблюдаемом minimal flowchart-output `xlink:href` не встретился. Прежде чем добавлять поддержку в `AotSafeSvgImage`, нужно проверить выход для sequence/class/state.

Эти наблюдения уходят в backlog M5; на M0 они не блокеры.

## Подтверждение для конституции MarkMello

| Правило | Статус |
| --- | --- |
| Mermaid не реализуется через WebView | OK — рендер in-process, SVG-output не требует браузера |
| Mermaid не реализуется через Node/npm/Puppeteer/Chromium | OK — в зависимости только BCL + Pidgin |
| Mermaid не реализуется через сетевой сервис | OK при условии, что CSS `@import` в выходе игнорируется нашим viewer (см. M5 риск) |
| Mermaid не реализуется через внешний процесс | OK |
| Naiad как обязательная runtime-зависимость | подтверждено — пакет совместим с net10, рендер успешный |

## Runtime decision (M0.5)

Из M0 вытекает обязательный upgrade solution на `net10.0`. Решение зафиксировано в ADR-0005 Decision 10 и оформлено как milestone M0.5 в `docs/implementation-plan-diagram-blocks-mermaid.md`.

Выполнено в рамках того же worktree:

- `Directory.Build.props`: `net9.0` → `net10.0`;
- `Microsoft.Extensions.DependencyInjection*` PackageReferences: `9.0.0` → `10.0.0`;
- `.github/workflows/release-windows.yml`: все три `dotnet-version: 9.0.x` → `10.0.x`;
- локально установлен .NET SDK `10.0.300` (winget `Microsoft.DotNet.SDK.10`).

Проверки:

| Команда | Результат |
| --- | --- |
| `dotnet restore MarkMello.sln` | OK, все 7 проектов восстановлены |
| `dotnet build MarkMello.sln -c Debug` | 0 warnings, 0 errors, ~27 c |
| `dotnet build src/MarkMello.Desktop -c Release` | 0 warnings, 0 errors, ~10 c |
| `dotnet test tests/MarkMello.Domain.Tests` | 18 passed |
| `dotnet test tests/MarkMello.Presentation.Tests` | 114 passed |

Полный AOT-publish не выполнялся (это M8 scope); Release-build стандартного pipeline проходит без предупреждений.

## Решение по продолжению

Продолжаем по ADR-0005. M0 закрыт. M0.5 (runtime upgrade) выполнен. Следующий шаг — M1 (общая модель `MarkdownDiagramBlock` + composition validation).

## Артефакты spike (`spike/`, gitignored)

- `spike/NaiadSpike/` — `net10.0` console с `Naiad 0.1.2`, рендерящий минимальный flowchart в `flowchart.svg`.
- `spike/Reflect/` — `net9.0` console с `System.Reflection.MetadataLoadContext`, использованный для снятия публичного API и references без исполнения net10-кода (на старте M0, когда SDK 10 ещё не было).

Папка не входит в solution, исключена через `spike/.gitignore`. Может быть удалена после старта M3 или сохранена как reproducible fixture.

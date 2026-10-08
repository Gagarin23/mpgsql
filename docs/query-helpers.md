# Отправка запросов и чтение результатов

`MpgsqlMessageSession` — владелец чтения и записи уже аутентифицированного
соединения PostgreSQL protocol 3.0 с `client_encoding=UTF8`. Ему передаются
`PipeReader` и `PipeWriter`. Открытие соединения, authentication, pool и
multiplexing scheduler пока остаются у вызывающего кода.

## Отдельные потоки отправки и чтения

**Крайне рекомендуется запускать отправку и чтение параллельно.**
`SendQueryAsync` принимает SQL с позиционными `$1`, `$2`, … и
`ReadOnlyMemory<MpgsqlParameterValue>`. Он ставит в FIFO очередь полный пакет
`Parse` → `Bind` → `Describe(portal)` → `Execute(maxRows=0)` без `Sync`.
Statement и portal безымянные; параметры и результаты binary.
Именованные параметры и переписывание SQL не поддерживаются.

`SendSyncAsync` вызывается **отдельно в том же потоке отправки после всех
`SendQueryAsync`**, включая отправки без немедленного `await`.
Здесь поток означает последовательность действий producer; async-код не
прикрепляется к одному OS thread. Порядок определяется вызовами методов,
а не порядком ожидания возвращённых задач. `SendSyncAsync` закрывает приём
новых запросов группы сразу и ставит ровно один `Sync` за её запросами.

`ReadResultsAsync` выполняет только чтение и может быть запущен до первой
отправки. `ReadAsync`, `NextResultAsync` и освобождение reader/группы также
ничего не записывают в транспорт. Внутри session постоянно работают
независимые reader и writer loops; пользовательские continuations и callbacks
не выполняются синхронно на сетевом reader loop.

```csharp
await using var session = new MpgsqlMessageSession(input, output, lifetimeToken);
await using var batch = session.CreateBatch(requestToken);

Task receiving = Task.Run(async () =>
{
    await using var reader = await batch.ReadResultsAsync();
    do
    {
        while (await reader.ReadAsync())
            Consume(reader.QueryIndex, reader.GetInt64(0));
    } while (await reader.NextResultAsync());
});

Task sending = Task.Run(async () =>
{
    try
    {
        // Не ждём каждую отправку сразу. Память сохраняется до её завершения.
        Task[] pending = values.Select(value =>
            batch.SendQueryAsync("select $1", new[] { MpgsqlParameterValue.Int64(value) })
                .AsTask()).ToArray();
        await Task.WhenAll(pending);
    }
    finally
    {
        // Даже логическая отмена группы не отменяет её Sync.
        await batch.SendSyncAsync();
    }
});

await Task.WhenAll(sending, receiving);
```

Допустимо вызвать `SendSyncAsync` сразу после постановки всех запросов,
затем дождаться их задач: FIFO гарантирует тот же порядок на проводе.
При ошибке формирования запроса producer всё равно должен закрыть уже
принятые отправки через `finally` с `SendSyncAsync`. После SQL-ошибки также
нельзя ждать завершения группы, не отправив её `Sync`.

Завершение отправки означает завершение клиентского `PipeWriter.FlushAsync`,
а не получение ответа PostgreSQL. Helper не добавляет frontend-сообщение
`Flush`: маленькие ответы сервер может буферизовать до `Sync`. Поэтому
producer не должен ждать результата запроса перед отправкой `Sync`.
Для управления `Flush`, именованными portals, частичным `Execute` и
нестандартными последовательностями есть raw protocol API на отдельно
управляемом соединении.

## Явные prepared statements

`session.CreatePreparedStatement(sql, parameterTypes)` создаёт локальный
`MpgsqlPreparedStatement`, не отправляя сообщений. Объект принадлежит одной
`MpgsqlMessageSession`; его нельзя передать другой сессии. `Name`, `Sql`,
`ParameterTypes` доступны для чтения. Имена `mpgsql_ps_<counter>` генерируются
сессией без повторного использования, а список OID копируется при создании.
Все OID должны быть ненулевыми; пустой список означает отсутствие параметров.
Пользователь передаёт полный список типов. Соответствие этой сигнатуры SQL
проверяет PostgreSQL; helper не разбирает SQL и не выводит дополнительные типы.

`batch.SendPrepareAsync(statement)` ставит только именованный `Parse`.
`batch.SendQueryAsync(statement, parameters)` ставит `Bind(statement)` →
`Describe(portal)` → `Execute(maxRows=0)` без повторного `Parse`. Portal
безымянный, параметры и результаты binary. Описание столбцов получается
для каждого выполнения; кеша описаний, SQL и автоматической подготовки нет.
Количество и OID значений проверяются относительно объявленной сигнатуры
до постановки отправки. `Int64Array` и `NullableInt64Array` имеют один OID 1016.
Параметры и исходная память заимствуются до завершения соответствующего
`SendQueryAsync`, включая отмену или ошибку. Encoder по-прежнему проверяет
полную вместимость destination до первой записи и не создаёт encoded buffers
параметров.

Каждый метод `Send…Async` завершается после клиентского `PipeWriter.FlushAsync`,
а задача `statement.Prepared` — при получении `ParseComplete`. **Перед ожиданием
`Prepared` producer обязан отправить явный `Sync`**: frontend `Flush` helper не
добавляет, и сервер может буферизовать подтверждение. До `ParseComplete`
выполнения допустимы только в batch подготовки, после поставленного `Parse`.
После подтверждения можно использовать statement в нескольких batch в полёте,
не ожидая предыдущего `ReadyForQuery`.

```csharp
var statement = session.CreatePreparedStatement("select $1", new uint[] { 20 });
await using var batch = session.CreateBatch(requestToken);

Task receiving = Task.Run(async () =>
{
    await using var reader = await batch.ReadResultsAsync();
    do
    {
        while (await reader.ReadAsync())
            Consume(reader.QueryIndex, reader.GetInt64(0));
    } while (await reader.NextResultAsync());
});

try
{
    Task[] sends =
    [
        batch.SendPrepareAsync(statement).AsTask(),
        batch.SendQueryAsync(statement, new[] { MpgsqlParameterValue.Int64(42) }).AsTask(),
        batch.SendQueryAsync(statement, new[] { MpgsqlParameterValue.Int64(43) }).AsTask()
    ];
    await Task.WhenAll(sends);
}
finally
{
    await batch.SendSyncAsync();
}
await receiving;
await statement.Prepared;
```

Reader хранит FIFO описаний опубликованных операций. Подготовка ожидает
`ParseComplete`, prepared execution начинает с `BindComplete`, закрытие
ожидает `CloseComplete`. Подготовка и закрытие не создают результатов и не
увеличивают `QueryIndex`; группа только с такими операциями читается как пустая.
Пользовательские continuations задачи `Prepared` не выполняются синхронно
на сетевом reader loop.

`batch.SendCloseAsync(statement)` ставит `Close(statement)` без `Sync` и сразу
запрещает новые выполнения. Уже принятые отправки сохраняют порядок. Окончание
отправки ещё не означает закрытие на сервере: для успешного подтверждения
наблюдайте `batch.Completion`. Close, отменённый до публикации или пропущенный
из-за предыдущей ошибки, можно повторить в новом batch после завершения старого.
До этой границы повторный Close запрещён. После полученного `CloseComplete`
повторный Close ничего не отправляет. После неуспешной подготовки Close также
ничего не отправляет: серверный statement не был создан.

`statement.Dispose()` локально инвалидирует handle и никогда не отправляет
сообщений. Он допустим до постановки подготовки, после её ошибки или отмены,
после подтверждённого Close либо остановки session; повторный Dispose допустим.
Для ещё не поставленного Parse задача `Prepared` получает `ObjectDisposedException`.
Если подготовка pending или успешна и Close ещё не подтверждён, Dispose бросает
`InvalidOperationException`, не меняя состояние. Завершите подготовку и явно
отправьте Close/Sync; для отменённого неопубликованного Parse серверный Close
не нужен. После Dispose дальнейшие SendPrepare/SendQuery/SendClose с этим handle
бросают `ObjectDisposedException`.

При SQL-ошибке задача неподтверждённой подготовки завершается ошибкой после
`Sync` / `ReadyForQuery`, включая Parse, пропущенный сервером. Уже полученный
`ParseComplete` сохраняет успешную подготовку, даже если следующий запрос
завершился ошибкой и implicit transaction откатилась. Отмена неопубликованного
Parse отменяет `Prepared`; отмена опубликованного Parse не отменяет ожидание
серверного подтверждения. EOF, transport failure и остановка session завершают
неподтверждённые задачи и запрещают дальнейшее использование её statements.
Повторная подготовка одного объекта запрещена; после неудачи создайте новый.
Автоматических повторов или восстановления удалённого серверного statement нет.

Серверный named statement живёт до явного Close или конца PostgreSQL-сессии.
Ещё не поставленные в очередь handles учитываются слабо: потеря пользовательских
ссылок позволяет GC освободить handle, SQL и копию OID без сетевых операций.
Живой локальный handle по-прежнему получает ошибку `Prepared` при остановке session.
Сильный registry начинает удержание только после успешной клиентской admission
SendPrepareAsync. Ошибка validation до admission оставляет handle локальным;
ошибка параметров выполнения не освобождает уже принятую подготовку.
Отмена неопубликованного Parse освобождает registry и ссылки queued work сразу;
ошибка или пропуск опубликованного Parse — на ReadyForQuery. Неуспешный handle
больше не удерживает batch подготовки/закрытия. Успешно подготовленные handles
остаются в registry до CloseComplete или остановки session, включая логическую
отмену опубликованного Parse и ошибку последующего выполнения.
Освобождение helper инвалидирует handles и завершает Pipe endpoints; владение
исходным stream, как и прежде, остаётся у adapter. Завершение серверной сессии
зависит от закрытия физического соединения.

## Группы, результаты и ошибки

`MpgsqlQueryBatch` объединяет запросы общей границей `Sync`/`ReadyForQuery`.
Это также общая граница ошибки и, вне явной транзакции, implicit transaction.
Одновременно собирается одна группа. После вызова её `SendSyncAsync` можно
отправлять следующую, не дожидаясь предыдущего `ReadyForQuery`. Несколько
закрытых групп могут находиться в полёте, а их результаты можно потреблять
независимо, включая обратный порядок групп.

`ReadResultsAsync` разрешён один раз на группу и ожидает описание первого
результата либо окончательное завершение пустой группы. `ReadAsync` читает
строки текущего результата; `NextResultAsync` пропускает оставшиеся строки
и переходит к следующему результату. Он может ждать дальнейших отправок
ещё открытой группы. `Columns`, `QueryIndex` (с нуля) и `CommandTag`
относятся к текущему результату. Команды без строк имеют пустые `Columns`.
Пустой SQL завершается `EmptyQueryResponse`, его `CommandTag` равен null.
Конкурентные операции движения/освобождения одного reader запрещены.

`Sealed` завершается, когда writer публикует `Sync`; `Completion` — только
после `ReadyForQuery`. Обе задачи независимы от request token.
`TransactionStatus` после завершения отражает `Idle`, `InTransaction` или
`FailedTransaction`; внутри явной транзакции ошибка требует `ROLLBACK`.

Reader проверяет отдельные подтверждения `ParseComplete`, `BindComplete`,
описание portal, поток строк и завершение команды. При `ErrorResponse` он
переходит в recovery, освобождает ещё не выданные результаты группы и
ждёт `ReadyForQuery`, не ожидая подтверждений пропущенных запросов.
После этой границы reader и `Completion` сообщают `MpgsqlServerException`
с `Diagnostics`, `SqlState`, `QueryIndex` и `TransactionStatus`.
Ошибки подготовки, Close и самого `Sync`, например deferred constraint failure,
имеют `QueryIndex=null`. Пропущенная подготовка сообщает ошибку того запроса,
который перевёл группу в recovery. Следующая группа продолжает обрабатываться.

`FATAL`/`PANIC` завершают session сразу после получения диагностики, в том числе
без активной группы. Severity берётся из invariant поля `V`, а при его отсутствии
из `S`. Если transport оборвался после обычного `ErrorResponse`, но до
`ReadyForQuery`, сохранённая диагностика также передаётся через
`MpgsqlServerException`. `Diagnostics` сохраняет все received fields, а `Message`
содержит исходное primary message сервера. `TransactionStatus` теперь nullable:
при terminal failure он равен `null`, при подтверждённом `ReadyForQuery` — его
статусу. Отдельная EOF/I/O/protocol ошибка, если она была получена после
диагностики, сохраняется в `InnerException`.

Terminal failure завершает `session.Completion` и все незавершённые группы,
подготовки и ожидающие отправки. У активной группы сохраняется её `QueryIndex`;
у session и соседних групп индекс равен `null`. Успешно завершённые группы
остаются завершёнными. Диагностика очищается только после принятого
`ReadyForQuery`, поэтому более поздний обрыв не использует старую SQL-ошибку.

`NoticeResponse`, `ParameterStatus` и `NotificationResponse` не меняют
состояние запроса. Есть очереди `TryReadNotice`, `TryReadNotification`
и текущие значения через `TryGetParameter`. COPY требует отдельного
COPY API и выделенного соединения. Неожиданный COPY, повреждённые
сообщения, EOF и transport error завершают session с ошибкой;
`session.Completion` сообщает её.

## Параметры и владение памятью

`MpgsqlParameterValue` — readonly struct без `object` inference и имён. Explicit
scalar/array factories покрывают все текущие `TypeOid`; Int64 остаётся inline,
остальные представления используют typed holders. См. [type-converters.md](type-converters.md).
`VarChar`, `BpChar`, `Name` принимают `string?`, а их `*Array` factories —
`ReadOnlyMemory<string?>?`. Typed getter читает `string` и nullable-reference
массивы; пробелы `bpchar` сохраняются, а длину проверяет PostgreSQL.
Сохранены `Int64(long?)`, `Int64Array(ReadOnlyMemory<long>?)` и
`NullableInt64Array(ReadOnlyMemory<long?>?)` с OID 20 и 1016.
`Int64Array(null)` обозначает SQL NULL всего массива; пустой
`ReadOnlyMemory<long>` — пустой массив; nullable elements — отдельная форма.
Для nullable переменной типа `long[]?` явно различайте null и непустое
значение перед преобразованием в memory: преобразование null массива
в `ReadOnlyMemory<long>` само по себе даёт пустую memory.
`default(MpgsqlParameterValue)` недопустим.

Helper сразу проверяет размеры и корректность параметров, затем writer
кодирует их непосредственно в одну reservation выходного Pipe и публикует
полный пакет одним `Advance`. Отдельных encoded buffers параметров нет.
Wire encoding: UTF8 C strings, big-endian числовые поля; длина frontend
сообщения включает 4 байта собственного length, но не type tag. Bind
содержит отдельный Int32 length каждого значения; SQL NULL — length -1.
Внутренний span encoder проверяет вместимость **до первой записи**;
при недостатке места выбрасывает `ArgumentException`, не меняя destination.

Массив descriptors и исходные массивы значений заимствуются до завершения
задачи **соответствующего `SendQueryAsync`**, включая отмену или ошибку.
До этого их нельзя менять, возвращать в pool или освобождать. Writer не
завершает задачу отменённой отправки, пока encoder использует её память.

Полученные строки могут приходить до начала потребления. Session сохраняет
их в неограниченной RAM-очереди: это осознанная политика первой версии.
Она требует ограничивать количество запросов в полёте и вовремя читать
результаты. Кодек ограничивает wire length одного сообщения 64 MiB.
Частичный frame собирается вне входного Pipe, поэтому сообщение крупнее
его pause threshold не блокирует доставку; готовый frame передаётся строке
без повторного копирования. Полный заимствованный DataRow копируется для
владения после `PipeReader.AdvanceTo`.

`GetRawValue` возвращает заимствованные bytes до следующего движения или
освобождения reader/группы. Для сохранения bytes сделайте копию.

Внутреннее хранилище строки переиспользуется только после освобождения её
владения. Копии handle содержат номер поколения: повторное освобождение старой
копии не затрагивает следующую строку. Пул ограничен 1024 пустыми хранилищами
на session и не удерживает payload. Для одного поля offset хранится непосредственно
в хранилище; для нескольких полей арендуется массив Int32. NULL и пустое значение
различаются, а count, длины полей и конец payload проверяются при построении индекса.

Уведомление ожидающего reader выполняется асинхронно после конца результата или
текущего transport read; при заполнении row budget — до ожидания свободных bytes.
Готовые события доступны сразу. Это сохраняет возможность потребления до ReadyForQuery
и не запускает пользовательские продолжения на receive loop.

`GetInt64` возвращает nullable scalar; массивные геттеры возвращают
самостоятельные decoded arrays, которые живут после движения reader.
Геттеры проверяют binary format и OID.

## Отмена и освобождение

Request token останавливает приём и ожидание результатов своей группы,
а не общий transport и не SQL на сервере. Не начатые отправки снимаются
с очереди. Опубликованные запросы продолжают выполняться; reader
освобождает накопленные строки и пропускает последующие DataRow без
копирования payload и вызова value converters. Фрагментированные
отбрасываемые строки проверяют count/length prefixes и границы frame.
Одна регистрация token приходится на группу, а не на каждую строку;
callback лишь помечает группу и ставит очистку в служебную очередь.

`SendSyncAsync` не подавляется отменой и остаётся разрешён после
`batch.DisposeAsync`. **Producer обязан вызвать его явно.**
Reader dispose прекращает потребление, сохраняя возможность producer
дослать запросы. Batch dispose также запрещает новые запросы, но уже
принятые отправки сохраняются. Если `Sync` уже поставлен и request не
отменён, dispose ждёт `Completion` и может сообщить ещё не наблюдённую
ошибку. При отмене dispose возвращается без ожидания долгого SQL;
physical owner отдельно наблюдает `Completion`. Lifetime token session
останавливает весь transport и все незавершённые группы.

Серверный `CancelRequest` пока не реализован этим helper. Он относится к
текущей команде физического backend и не обеспечивает отмену всех отдельных
Sync segments конвейера. Для будущего mux отмена SQL должна использовать
выделенные соединения, объединение отмен по connection, ограниченные окно
in-flight и concurrency cancel connections. Это не связывает token
логического запроса с lifetime общего reader loop.

## Проверки и измерения

Unit tests проверяют полный outbound payload, порядок отправок без await,
отдельный Sync, state transitions, ошибки на каждом этапе, асинхронные
сообщения, одиночную/массовую отмену и fragmented rows. Integration harness
проверяет эти пути на живом PostgreSQL, включая deferred error на Sync.

`PreparedStatementTests` проверяет named wire packets, подготовку с несколькими
выполнениями, смешанные операции, несколько групп в полёте, владельца и OID,
ошибки/пропуск Parse и Close, отмену до и после публикации и остановку session.
`PreparedStatementChecks` на PostgreSQL проверяет повторные binary выполнения,
NULL/arrays, состояние `pg_prepared_statements`, сохранение statement после
ошибки выполнения, пропущенное закрытие и повторный Close после recovery.

Проверка prepared statements 2026-10-02: solution build без ошибок и
предупреждений, все 736 unit tests пройдены; полный live harness пройден на
PostgreSQL 17.11 с SCRAM-SHA-256, включая новые prepared statement сценарии.

`ResultBufferBenchmarks` сравнивает сохранение и отбрасывание 1/128 строк
с payload 8/98324 bytes, целыми frame и фрагментами по 4096 bytes.
Он измеряет только framing/ownership, исключая сеть, очередь session и
dispatch отмены. Это не benchmark end-to-end throughput драйвера.

Предыдущий запуск ResultBufferBenchmarks: PostgreSQL 17.11, SCRAM-SHA-256; solution build без
ошибок и предупреждений, 634 unit tests. Короткий BenchmarkDotNet 0.15.8
запуск на .NET 10.0.12 x64, InProcessEmitToolchain (3 warmup, 5 iterations
по 100 ms) дал для 128 строк по 98324 bytes: сохранение/отбрасывание
242/6.82 µs для целых frame и 323/106 µs для фрагментов 4096 bytes.
Это измерение горячего framing/ownership пути с прогретыми pools.
MemoryDiagnoser показал примерно 8.2 KiB на сохранение 128 строк и
0–5 bytes на отбрасывание; малые значения in-process включают шум harness.
Итоговый throughput SQL, сеть и latency dispatch отмены здесь не измерялись.

```powershell
dotnet run --project benchmarks/Mpgsql.Benchmarks -c Release -- `
    --filter '*ResultBufferBenchmarks*' --inProcess --job Short `
    --warmupCount 3 --iterationCount 5 --iterationTime 100 --launchCount 1
```

Официальные
правила: [Extended Query и pipeline flow](https://www.postgresql.org/docs/17/protocol-flow.html#PROTOCOL-FLOW-EXT-QUERY),
[параллельные отправка/чтение в pipeline mode](https://www.postgresql.org/docs/17/libpq-pipeline-mode.html#LIBPQ-PIPELINE-INTERLEAVING),
[ограничения серверной отмены](https://www.postgresql.org/docs/17/libpq-cancel.html).

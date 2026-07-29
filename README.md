# Db2i

[![CI](https://github.com/bostick23/ibm.db2.dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/bostick23/ibm.db2.dotnet/actions/workflows/ci.yml)

Provider ADO.NET open source per Db2 su IBM i (AS/400, iSeries), basato sul protocollo
host-server usato da [JTOpen](https://github.com/IBM/JTOpen).

> Stato: **pre-alpha**. `0.5.0-alpha.1` aggiunge pooling delle sessioni e
> `Db2iDataSource`, verificati anche contro IBM i reale.

## Connessione

Il provider apre direttamente una sessione con il database host server,
senza installare driver IBM sul client:

```csharp
await using var connection = new Db2iConnection(
    "Server=ibmi.example.test;User ID=MYUSER;Password=secret;Default Collection=MYLIB");

await connection.OpenAsync();

Console.WriteLine(connection.ServerVersion);
Console.WriteLine(connection.ServerCcsid);
Console.WriteLine(connection.ServerJobIdentifier);
```

Una query parametrizzata usa marker posizionali `?`; il nome del parametro è
accettato dall'API ADO.NET ma non cambia la posizione sul wire:

```csharp
await using var connection = new Db2iConnection(
    "Server=ibmi.example.test;User ID=MYUSER;Password=secret;Default Collection=MYLIB");

await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = "select CUSNUM, LSTNAM from QCUSTCDT where CUSNUM = ?";
command.Parameters.Add("p1", 938472);

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
}
```

`ExecuteNonQuery` usa gli stessi marker posizionali e restituisce il numero di
righe indicato da Db2 for i:

```csharp
await using var command = connection.CreateCommand();
command.CommandText = "update MYLIB.CUSTOMERS set STATUS = ? where CUSNUM = ?";
command.Parameters.Add("status", "A");
command.Parameters.Add("customer", 938472);

var affected = await command.ExecuteNonQueryAsync();
```

Per una transazione locale, ogni comando deve riferirsi esplicitamente alla
transazione attiva:

```csharp
await using var transaction =
    (Db2iTransaction)await connection.BeginTransactionAsync(
        IsolationLevel.ReadCommitted);

await using var command = new Db2iCommand(
    "delete from MYLIB.WORK_ROWS where BATCH_ID = ?",
    connection)
{
    Transaction = transaction,
};
command.Parameters.Add("batch", 42);

await command.ExecuteNonQueryAsync();
await transaction.CommitAsync();
```

Per condividere un pool con ownership e dispose espliciti:

```csharp
await using var dataSource = new Db2iDataSource(
    "Server=ibmi.example.test;User ID=MYUSER;Password=secret;Max Pool Size=20");

await using var connection = await dataSource.OpenConnectionAsync();
```

`DbDataSource.CreateCommand()` è supportato e apre/restituisce automaticamente
una connessione del pool per ogni esecuzione.

## Architettura

Il provider usa il database host server IBM i, non DRDA:

```text
DbConnection / DbCommand / DbDataReader
                 |
          sessione SQL IBM i
                 |
   Client Access data stream (big-endian)
                 |
       TCP 8471 oppure TLS 9471
```

Le responsabilità sono separate:

- `Db2i*`: contratto pubblico ADO.NET basato su `System.Data.Common`;
- `Db2i.Protocol`: framing, autenticazione, richieste SQL e decodifica delle reply;
- test unitari: vettori binari e server host simulato, senza richiedere IBM i;
- test di integrazione reali: opt-in tramite variabili d'ambiente.

## Stato implementativo

- [x] `DbProviderFactory`, `DbConnection`, `DbCommand`, `DbParameter`,
  `DbParameterCollection`, `DbTransaction` e `DbDataReader`;
- [x] connection string con alias ADO.NET, porte 8471/9471 e timeout;
- [x] header Client Access da 20 byte e codec di pacchetto con limiti di sicurezza;
- [x] trasporto TCP/TLS;
- [x] richiesta/reply `exchange random seeds` (`0x7001`/`0xF001`);
- [x] password substitute per QPWDLVL 0-4 e richiesta `start server`;
- [x] set degli attributi SQL e acquisizione di CCSID/VRM/job;
- [x] lifecycle `Open`/`OpenAsync`/`Close`, timeout e cancellazione;
- [x] TLS con validazione del certificato attiva per default;
- [x] `Prepare`, `ExecuteReader` ed `ExecuteScalar`, sync e async;
- [x] marker `?` con parametri input espliciti, inferiti e NULL tipizzati;
- [x] fetch streaming in blocchi da circa 32 KiB e un reader attivo per connessione;
- [x] `CommandBehavior.SingleRow`, `SchemaOnly`, `SequentialAccess` e
  `CloseConnection`;
- [x] `SMALLINT`, `INTEGER`, `BIGINT`, `DECIMAL`, `REAL`, `DOUBLE`, `CHAR`,
  `VARCHAR`, `DATE`, `TIME`, `TIMESTAMP`, `BINARY` e `VARBINARY`;
- [x] diagnostica `SQLCODE`, `SQLSTATE` e testo IBM i su `Db2iException`;
- [x] `ExecuteNonQuery`, conteggio righe da SQLCA e autocommit;
- [x] transazioni locali con commit, rollback e livelli di isolamento;
- [x] commit e rollback verificati su una tabella journaled di IBM i reale;
- [x] `DbCommand.Cancel`, timeout e cancellazione tramite una sessione ausiliaria;
- [x] pooling globale e pool dedicati a `Db2iDataSource`;
- [x] `DbProviderFactory.CreateDataSource`, clear dei pool e reset sicuro;
- [ ] LOB, stored procedure, parametri output e batch.

La roadmap con i criteri di accettazione è in [docs/roadmap.md](docs/roadmap.md).
Lo stato operativo e le attività in corso sono aggiornati in [TODO.md](TODO.md).

## Build

Richiede .NET SDK 10 per compilare tutti i target:

```powershell
dotnet test Db2i.sln --configuration Release
dotnet pack src/Db2i/Db2i.csproj --configuration Release
```

La libreria è multi-target `net8.0` e `net10.0`.

## Connection string

```text
Server=my-ibmi;
User ID=MYUSER;
Password=secret;
Database=RDBNAME;
Default Collection=MYLIB;
SSL=true;
Trust Server Certificate=false;
Port=9471;
Connect Timeout=15;
Pooling=true;
Max Pool Size=100
```

`Port` è facoltativo: il default è 8471, oppure 9471 quando `SSL=true`.
Sono riconosciuti anche alias comuni come `Data Source`, `UID`, `PWD`,
`Initial Catalog` e `Current Schema`.

La validazione del certificato TLS è obbligatoria per default.
`Trust Server Certificate=true` la disabilita esplicitamente e dovrebbe essere
usato solo in ambienti controllati. `Connect Timeout=0` indica timeout infinito.

Il pooling è attivo per default. `Max Pool Size` limita il totale delle sessioni
fisiche attive e inattive; `Connect Timeout` include anche l'attesa di uno slot.
`Pooling=false` ripristina la chiusura fisica a ogni `Close`. Le connessioni
normali condividono pool globali per impostazioni equivalenti; ogni
`Db2iDataSource` possiede invece un pool isolato, chiuso dal suo dispose.
`Db2iConnection.ClearPool` e `ClearAllPools` invalidano anche le sessioni
attualmente in uso, che saranno eliminate al successivo `Close`.

Prima di riutilizzare una sessione, il provider esegue rollback se necessario,
ripristina autocommit ed elimina statement e descriptor rimasti. Una sessione
occupata, interrotta o non sincronizzata viene scartata. M4.1 non implementa
prewarming, pool minimo o scadenza automatica delle sessioni inattive.

Il provider usa SQL naming e supporta IBM i 7.3 o successivo con password di
sistema QPWDLVL 0-4. DRDA, naming `*SYS`, MFA, Kerberos e token di profilo non
fanno parte di questo milestone.

`CommandTimeout=0` indica durata illimitata. M3 invia il comando host-server
`CANCEL` da una seconda sessione autenticata e drena la reply primaria: quando
questa risincronizzazione riesce, la connessione resta aperta. Se `CANCEL` o il
drain falliscono, la connessione viene chiusa per sicurezza.

Sono supportati `ReadUncommitted`, `ReadCommitted`, `RepeatableRead` e
`Serializable`; `Unspecified` usa `ReadCommitted`. La tabella coinvolta deve
essere journaled su IBM i per usare commitment control. `Chaos`, `Snapshot`,
`CommandType.StoredProcedure`, `CALL`, batch, savepoint, transazioni distribuite
e parametri non-input non sono supportati.

## Test contro IBM i

I test locali usano un database host server simulato anche per TLS. Per abilitare
le prove reali, valorizzare una o entrambe le variabili senza salvarle nel
repository:

```powershell
$env:DB2I_TEST_TCP_CONNECTION_STRING = "Server=...;User ID=...;Password=..."
$env:DB2I_TEST_TLS_CONNECTION_STRING = "Server=...;User ID=...;Password=...;SSL=true"
$env:DB2I_TEST_DML_ENABLED = "true"
$env:DB2I_TEST_DML_TABLE = "MYLIB.DB2I_DML_TEST"
$env:DB2I_TEST_DML_ID_COLUMN = "ID"
$env:DB2I_TEST_DML_DESCRIPTION_COLUMN = "DESCRIPTION"
$env:DB2I_TEST_DML_DATE_COLUMN = "DATE_VALUE"
$env:DB2I_TEST_DML_DECIMAL_COLUMN = "DECIMAL_VALUE"
dotnet test Db2i.sln --configuration Release --filter Category=Integration
```

Se una variabile non è valorizzata, il relativo caso non apre alcuna connessione.
Il test TCP M2 esegue query in lettura. I test M3 scrivono soltanto quando
`DB2I_TEST_DML_ENABLED=true` e tutti gli identificatori della tabella autorizzata
sono configurati. Gli identificatori accettano soltanto nomi SQL semplici; il
test verifica prima una firma di quattro colonne (`DECIMAL(5,0)`, `CHAR(100)`,
`DECIMAL(5,0)`, `DECIMAL(9,4)`), usa marcatori univoci e pulisce in `finally`.
Il test transazionale verifica anche il journaling prima di scrivere. Le
credenziali e i nomi dell'ambiente reale non devono essere salvati nel progetto.
I test M4 aggiungono il riuso dello stesso job IBM i e il rollback prima del
rientro nel pool; entrambi sono stati verificati su IBM i reale il 29 luglio
2026.

## Provenienza e licenza

Il progetto è un porting derivato da JTOpen. Di conseguenza è distribuito sotto
IBM Public License 1.0, come il codice originale. I file di protocollo indicano
le classi JTOpen dalle quali derivano.

Questo progetto non è affiliato né approvato da IBM. IBM, IBM i, AS/400, iSeries
e Db2 sono marchi dei rispettivi titolari.

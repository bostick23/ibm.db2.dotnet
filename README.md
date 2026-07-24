# Db2i

Provider ADO.NET open source per Db2 su IBM i (AS/400, iSeries), basato sul protocollo
host-server usato da [JTOpen](https://github.com/IBM/JTOpen).

> Stato: **pre-alpha**. L'API `System.Data.Common` e le prime primitive del
> protocollo sono disponibili, ma `Db2iConnection.Open()` non completa ancora
> autenticazione e apertura della sessione SQL.

## Obiettivo dell'MVP

Il primo traguardo è eseguire questa sequenza senza driver IBM installati sul client:

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

Questa è l'API di destinazione; al momento `OpenAsync` termina con una
`Db2iException` esplicita finché il milestone di connessione non è completo.

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
- test unitari: vettori binari del protocollo, senza richiedere un sistema IBM i;
- test di integrazione futuri: attivati solo tramite variabili d'ambiente.

## Stato implementativo

- [x] `DbProviderFactory`, `DbConnection`, `DbCommand`, `DbParameter`,
  `DbParameterCollection`, `DbTransaction` e `DbDataReader`;
- [x] connection string con alias ADO.NET, porte 8471/9471 e timeout;
- [x] header Client Access da 20 byte e codec di pacchetto con limiti di sicurezza;
- [x] trasporto TCP/TLS;
- [x] richiesta/reply `exchange random seeds` (`0x7001`/`0xF001`);
- [ ] password substitute per QPWDLVL 0-4 e richiesta `start server`;
- [ ] set degli attributi SQL e acquisizione di CCSID/VRM/job;
- [ ] prepare, execute, fetch e close;
- [ ] conversioni dei tipi SQL e transazioni.

La roadmap con i criteri di accettazione è in [docs/roadmap.md](docs/roadmap.md).

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
Port=9471;
Connect Timeout=15
```

`Port` è facoltativo: il default è 8471, oppure 9471 quando `SSL=true`.
Sono riconosciuti anche alias comuni come `Data Source`, `UID`, `PWD`,
`Initial Catalog` e `Current Schema`.

## Provenienza e licenza

Il progetto è un porting derivato da JTOpen. Di conseguenza è distribuito sotto
IBM Public License 1.0, come il codice originale. I file di protocollo indicano
le classi JTOpen dalle quali derivano.

Questo progetto non è affiliato né approvato da IBM. IBM, IBM i, AS/400, iSeries
e Db2 sono marchi dei rispettivi titolari.

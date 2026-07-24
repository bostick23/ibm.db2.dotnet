# Roadmap

## M0 — Fondamenta del provider

Completato nello scaffold iniziale:

- superficie ADO.NET compilabile su .NET 8 e .NET 10;
- parser della connection string;
- framing Client Access, TCP/TLS ed exchange dei random seed;
- test unitari deterministici del wire format.

## M1 — Connessione SQL autenticata

Criteri di accettazione:

1. supportare QPWDLVL 0/1 (DES), 2/3 (SHA-1) e 4 (PBKDF2-HMAC-SHA-512);
2. inviare `start server` al servizio database e validarne il return code;
3. inviare il set minimo di attributi SQL;
4. acquisire CCSID, VRM, livello funzionale e identificativo del job;
5. portare `Db2iConnection.State` a `Open` soltanto dopo una reply SQL valida;
6. chiudere socket e job in ogni percorso di errore o cancellazione;
7. verificare connessione TCP e TLS contro almeno due release IBM i supportate.

## M2 — Primo percorso query

Criteri di accettazione:

1. `ExecuteReader` per un `SELECT` senza parametri;
2. prepare/execute con marker posizionali `?`;
3. fetch a blocchi e `CommandBehavior.CloseConnection`;
4. tipi iniziali: `SMALLINT`, `INTEGER`, `BIGINT`, `DECIMAL`, `REAL`, `DOUBLE`,
   `CHAR`, `VARCHAR`, `DATE`, `TIME`, `TIMESTAMP`, `BINARY`, `VARBINARY`;
5. SQLSTATE, SQLCODE e testo diagnostico esposti tramite `Db2iException`;
6. test di integrazione per null, CCSID non 37 e nomi colonna.

## M3 — DML e transazioni

- `ExecuteNonQuery`, autocommit, commit e rollback;
- livelli di isolamento ADO.NET mappati su commitment control IBM i;
- timeout e cancel;
- gestione coerente di statement e cursor durante dispose/rollback.

## M4 — Compatibilità applicativa

- pooling;
- `DbDataSource`;
- batch;
- LOB e locator;
- stored procedure e parametri output;
- metadata/schema;
- integrazione con Dapper e, separatamente, un provider EF Core.

## Decisioni rinviate

- supporto DRDA sulle porte 446/448;
- autenticazione Kerberos, profile token e identity token;
- naming convention `*SYS`;
- record-level access e gli altri servizi Toolbox.

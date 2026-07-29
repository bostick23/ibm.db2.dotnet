# Roadmap

## M0 — Fondamenta del provider

Completato nello scaffold iniziale:

- superficie ADO.NET compilabile su .NET 8 e .NET 10;
- parser della connection string;
- framing Client Access, TCP/TLS ed exchange dei random seed;
- test unitari deterministici del wire format.

## M1 — Connessione SQL autenticata

Implementazione completata in `0.2.0-alpha.1`:

- [x] supportare QPWDLVL 0/1 (DES), 2/3 (SHA-1) e 4
  (PBKDF2-HMAC-SHA-512);
- [x] inviare `start server` al servizio database e validarne il return code;
- [x] inviare il set minimo di attributi SQL;
- [x] acquisire CCSID, VRM, livello funzionale e identificativo del job;
- [x] portare `Db2iConnection.State` a `Open` soltanto dopo una reply SQL valida;
- [x] chiudere socket e job in ogni percorso di errore o cancellazione;
- [x] verificare TCP e TLS con un server host simulato;
- [x] verificare TCP contro un IBM i reale, incluso CCSID 280;
- [ ] verificare TLS contro un IBM i 7.3+ reale.

## M2 — Primo percorso query

Implementazione completata in `0.3.0-alpha.1`:

- [x] `Prepare`, `ExecuteReader` ed `ExecuteScalar` sincroni e asincroni;
- [x] prepare/execute con marker posizionali `?`, inferenza `DbType`,
  `Size`/`Precision`/`Scale` e NULL tipizzati;
- [x] fetch streaming a blocchi e `CommandBehavior.CloseConnection`;
- [x] tipi iniziali: `SMALLINT`, `INTEGER`, `BIGINT`, `DECIMAL`, `REAL`,
  `DOUBLE`, `CHAR`, `VARCHAR`, `DATE`, `TIME`, `TIMESTAMP`, `BINARY`,
  `VARBINARY`;
- [x] SQLSTATE, SQLCODE e testo diagnostico esposti tramite `Db2iException`;
- [x] reader forward-only esclusivo per connessione e cleanup di cursor,
  descriptor e RPB;
- [x] timeout/cancellazione I/O con chiusura conservativa della connessione;
- [x] test simulati TCP/TLS e test TCP reale read-only per null, CCSID 280,
  nomi colonna, parametri e fetch multi-blocco.

## M3 — DML e transazioni

Implementazione disponibile in `0.4.0-alpha.1`:

- [x] `ExecuteNonQuery` sincrono/asincrono, marker input e righe interessate
  lette da `SQLERRD3`;
- [x] autocommit esplicito, commit, rollback e rollback su dispose;
- [x] `ReadUncommitted`, `ReadCommitted`, `RepeatableRead` e `Serializable`
  mappati su commitment control IBM i;
- [x] una sola transazione locale per connessione e associazione obbligatoria
  `DbCommand.Transaction`;
- [x] comando host-server `CANCEL` su sessione autenticata ausiliaria, drain
  della reply e riuso sicuro della connessione;
- [x] test simulati per DML, transazioni, timeout/cancel e perdita di
  sincronizzazione;
- [x] test DML autocommit contro IBM i reale con firma tabella e cleanup;
- [x] commit e rollback reali contro la tabella journaled autorizzata, inclusa
  la verifica dei dati dopo ogni boundary;
- [x] fallback compatibile con JTOpen quando IBM i rifiuta il cambio della
  persistenza dei locator durante l'avvio del commitment control.

## M4 — Compatibilità applicativa

- [x] pooling globale con limite, timeout, reset e clear;
- [x] `DbDataSource` con pool dedicato e supporto tramite `DbProviderFactory`;
- [x] collaudo pooling TCP e transazionale contro IBM i reale;
- [ ] batch;
- [ ] LOB e locator;
- [ ] stored procedure e parametri output;
- [ ] metadata/schema;
- [ ] integrazione con Dapper e, separatamente, un provider EF Core.

## Decisioni rinviate

- supporto DRDA sulle porte 446/448;
- autenticazione Kerberos, profile token e identity token;
- naming convention `*SYS`;
- record-level access e gli altri servizi Toolbox.

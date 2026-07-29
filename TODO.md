# Stato dei lavori

Ultimo aggiornamento: 29 luglio 2026.

Questo file registra lo stato operativo del progetto. La roadmap definisce i
milestone; qui vengono aggiornate le attività durante l'implementazione.

## Stato corrente

- Milestone attivo: **M4 — Compatibilità applicativa**.
- Fase corrente: **M4.1 pooling e `DbDataSource` completati e collaudati su
  IBM i reale**.
- Ultimo milestone completato: **M3 — DML e transazioni**,
  versione `0.4.0-alpha.1`.
- Versione corrente: **`0.5.0-alpha.1`**.
- Verifica corrente: 104 test automatici Release superati; build .NET 8 e
  .NET 10; categoria Integration 12/12, con 11 casi TCP reali per query, DML,
  transazioni, riuso del job e rollback al rientro nel pool. Il caso TLS non
  apre connessioni perché la relativa variabile non è configurata.

## Milestone

- [x] M0 — Fondamenta del provider.
- [x] M1 — Connessione SQL autenticata.
- [x] M2 — Primo percorso query.
- [x] M3 — DML e transazioni.
- [ ] M4 — Compatibilità applicativa.

Rimane separatamente da verificare TLS contro un IBM i reale; non blocca M3.

## M4.1 — Pooling e DbDataSource

### 1. Pooling

- [x] Aggiungere `Pooling` e `Max Pool Size` alla connection string.
- [x] Implementare pool globali per impostazioni effettive equivalenti.
- [x] Applicare il limite anche ai waiter e includere l'attesa nel connect timeout.
- [x] Gestire cancellazione, clear generazionale e scarto delle sessioni guaste.
- [x] Eseguire rollback, ripristino autocommit e cleanup degli statement prima
  del riuso.

### 2. DbDataSource

- [x] Implementare `Db2iDataSource` con pool dedicato e connection string immutabile.
- [x] Collegare `Db2iProviderFactory.CreateDataSource`.
- [x] Supportare i comandi connectionless forniti da `DbDataSource`.
- [x] Chiudere deterministicamente le sessioni inattive al dispose.

### 3. Verifica e rilascio

- [x] Coprire riuso, cap, timeout, cancellazione, reset, clear e dispose con il
  server simulato.
- [x] Aggiungere test opt-in per riuso del job IBM i e rollback della transazione
  al rientro nel pool.
- [x] Eseguire 104 test Release e compilare .NET 8/.NET 10.
- [x] Eseguire i due test pooling contro IBM i reale.
- [x] Generare e verificare il pacchetto NuGet `0.5.0-alpha.1`.

Il 29 luglio 2026 il pool ha riutilizzato lo stesso job IBM i e ha eseguito il
rollback di una transazione abbandonata sulla tabella journaled autorizzata
prima di consegnare la sessione alla connessione successiva.

## M3 — DML e transazioni

### 1. Analisi del protocollo

- [x] Individuare in JTOpen richieste e reply per execute immediato/preparato,
  commit, rollback e cancel.
- [x] Definire la lettura del numero di righe interessate da SQLCA.
- [x] Definire la mappatura tra `IsolationLevel` ADO.NET e commitment control
  IBM i.
- [x] Definire gli stati ammessi di connessione, comando, transazione e reader.

### 2. ExecuteNonQuery

- [x] Implementare `ExecuteNonQuery` e `ExecuteNonQueryAsync`.
- [x] Supportare marker `?` e gli stessi parametri input di M2.
- [x] Restituire correttamente il numero di righe interessate.
- [x] Gestire warning, errori SQL, timeout e cancellazione.
- [x] Aggiungere test con server simulato.

### 3. Transazioni

- [x] Implementare `BeginTransaction` e il percorso asincrono ADO.NET.
- [x] Implementare autocommit.
- [x] Implementare `Commit`/`CommitAsync`.
- [x] Implementare `Rollback`/`RollbackAsync`.
- [x] Applicare e validare i livelli di isolamento supportati.
- [x] Impedire transazioni concorrenti sulla stessa connessione.
- [x] Aggiungere test di commit, rollback, dispose e connessione interrotta.

### 4. Cancel e lifecycle

- [x] Implementare il comando host-server `CANCEL`.
- [x] Mantenere utilizzabile la connessione dopo una cancellazione riuscita.
- [x] Chiudere in modo coerente statement e cursor su rollback/dispose.
- [x] Conservare la chiusura di sicurezza della connessione quando il protocollo
  non può essere risincronizzato.

### 5. Verifica e rilascio

- [x] Eseguire l'intera suite Release su .NET 8 e compilare anche per .NET 10.
- [x] Aggiungere e verificare il test TCP reale DML autocommit sulla tabella
  autorizzata.
- [x] Verificare commit/rollback reali dopo l'attivazione del journaling.
- [x] Aggiornare README e roadmap.
- [x] Generare il pacchetto NuGet M3 `0.4.0-alpha.1`.

## Dipendenze per il collaudo reale M3

La tabella autorizzata e le sue quattro colonne sono configurate esclusivamente
tramite variabili d'ambiente locali. Il test ne valida firma e journaling prima
di ogni scrittura, usa un ID e un marcatore univoci e ripulisce in `finally`.
Al 24 luglio 2026 sono stati verificati DML autocommit, rollback con assenza
della riga e commit `Serializable` con persistenza della riga.

IBM i può rifiutare con return code `-601` il cambio della persistenza dei
locator dopo l'esecuzione di uno statement. Il provider replica il fallback di
JTOpen: disabilita quell'opzione per la sessione e ripete il cambio di modalità
transazionale senza il relativo code point. Nessuna credenziale è salvata nei
file del progetto.

## Criterio di completamento M3

Completato: `ExecuteNonQuery`, autocommit, transazioni e `CANCEL` sono
disponibili tramite `System.Data.Common`, coperti dal server simulato e
verificati su IBM i reale nell'area dati autorizzata.

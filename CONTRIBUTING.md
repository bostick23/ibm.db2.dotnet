# Contribuire

Ogni modifica al wire protocol deve includere:

1. il riferimento alla classe o al metodo JTOpen confrontato;
2. almeno un test con byte attesi deterministici;
3. limiti espliciti per lunghezze e allocazioni provenienti dalla rete;
4. cancellazione e cleanup verificati per il codice I/O;
5. nessuna password, seed o token nei log.

Per i test contro un IBM i reale non inserire credenziali nel repository.
I test di integrazione leggono, in modo opt-in:

- `DB2I_TEST_TCP_CONNECTION_STRING`;
- `DB2I_TEST_TLS_CONNECTION_STRING`;
- `DB2I_TEST_DML_ENABLED`;
- `DB2I_TEST_DML_TABLE`;
- `DB2I_TEST_DML_ID_COLUMN`;
- `DB2I_TEST_DML_DESCRIPTION_COLUMN`;
- `DB2I_TEST_DML_DATE_COLUMN`;
- `DB2I_TEST_DML_DECIMAL_COLUMN`.

Le variabili assenti non causano connessioni esterne e nessuna connection string
viene scritta nell'output dei test.

I test DML sono disattivati salvo opt-in esplicito con
`DB2I_TEST_DML_ENABLED=true`, accettano soltanto identificatori SQL semplici,
validano la struttura configurata prima di scrivere e ripuliscono ogni riga
creata. I test di transazione reale richiedono inoltre che la tabella sia
journaled.

Eseguire prima di aprire una pull request:

```powershell
dotnet test Db2i.sln --configuration Release
```

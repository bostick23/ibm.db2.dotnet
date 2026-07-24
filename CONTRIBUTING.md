# Contribuire

Ogni modifica al wire protocol deve includere:

1. il riferimento alla classe o al metodo JTOpen confrontato;
2. almeno un test con byte attesi deterministici;
3. limiti espliciti per lunghezze e allocazioni provenienti dalla rete;
4. cancellazione e cleanup verificati per il codice I/O;
5. nessuna password, seed o token nei log.

Per i test contro un IBM i reale non inserire credenziali nel repository.
L'infrastruttura dei test di integrazione userà variabili d'ambiente e dovrà
essere opt-in.

Eseguire prima di aprire una pull request:

```powershell
dotnet test Db2i.sln --configuration Release
```

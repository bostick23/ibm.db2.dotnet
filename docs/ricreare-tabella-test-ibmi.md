# Preparare tutte le strutture per i test IBM i

La suite usa due tabelle dedicate nella libreria esistente `PRO90DAT`:

| Oggetto | Utilizzo |
|---|---|
| `PRO90DAT.CLAUD00F` | DML, transazioni, rollback nel pool e batch DML |
| `PRO90DAT.DB2ILOBT` | Upload e download di BLOB/CLOB/DBCLOB e rollback LOB |
| Journal esistente oppure `PRO90DAT/DB2ITJRN` | Journaling delle due tabelle |
| `PRO90DAT/DB2ITRCV01` | Receiver iniziale, se si crea il journal dedicato |

Connessione, query scalari, batch di lettura e riuso del job non richiedono
altre tabelle applicative. Le query di lettura usano `SYSIBM.SYSDUMMY1` e i
cataloghi `QSYS2`, già forniti da IBM i. I test locali con server simulato non
richiedono strutture su IBM i.

Eseguire il SQL tramite **IBM i Access Client Solutions → Run SQL Scripts**,
con naming SQL. I comandi CL possono essere eseguiti da una sessione IBM i.
La libreria `PRO90DAT` deve già esistere e l'utente che prepara l'ambiente deve
avere l'autorità per creare la tabella e configurare il journaling.

## 1. Tabella DML

Se `CLAUD00F` è già stata ricreata e validata, passare al punto 2. Eseguire
questo SQL soltanto quando la tabella manca; crea una tabella vuota.

```sql
CREATE TABLE PRO90DAT.CLAUD00F (
    IDTBCL DECIMAL(5, 0) NOT NULL,
    DESCCL CHAR(100) NOT NULL,
    DT01CL DECIMAL(5, 0) NOT NULL,
    DECICL DECIMAL(9, 4) NOT NULL
);
```

La struttura deve contenere esattamente queste quattro colonne, in questo
ordine. `DT01CL` è un campo numerico `DECIMAL(5,0)`: benché la variabile di
configurazione si chiami `DB2I_TEST_DML_DATE_COLUMN`, i test richiedono questo
formato. Non occorre inserire righe iniziali.

## 2. Tabella LOB

Eseguire quando `DB2ILOBT` non esiste:

```sql
CREATE TABLE PRO90DAT.DB2ILOBT (
    ID VARCHAR(32) NOT NULL PRIMARY KEY,
    BLOB_VALUE BLOB(32M),
    CLOB_VALUE CLOB(256K) CCSID 1208,
    DBCLOB_VALUE DBCLOB(256K) CCSID 1200
);
```

Il nome `DB2ILOBT` rientra nei dieci caratteri dei nomi di sistema IBM i,
quindi può essere usato anche nei comandi CL di journaling. Mantenere queste
quattro colonne nell'ordine indicato. Il BLOB da 32 MiB contiene il payload
di test da oltre 17 MiB; CLOB e DBCLOB superano i limiti minimi verificati
dalla suite. Non servono righe iniziali.

Il CLOB usa UTF-8 (1208) e il DBCLOB UTF-16 (1200). La sintassi dei tipi LOB e
dei CCSID è documentata da IBM in
[CREATE TABLE](https://www.ibm.com/docs/en/i/7.5.0?topic=statements-create-table).

## 3. Verificare le strutture

Per la tabella DML:

```sql
SELECT COLUMN_NAME, DATA_TYPE, LENGTH,
       NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE
FROM QSYS2.SYSCOLUMNS
WHERE TABLE_SCHEMA = 'PRO90DAT'
  AND TABLE_NAME = 'CLAUD00F'
ORDER BY ORDINAL_POSITION;
```

Risultato atteso:

| Colonna | Tipo | LENGTH | Precisione | Scala | Nullable |
|---|---|---:|---:|---:|---|
| IDTBCL | DECIMAL | 5 | 5 | 0 | N |
| DESCCL | CHAR | 100 | NULL | NULL | N |
| DT01CL | DECIMAL | 5 | 5 | 0 | N |
| DECICL | DECIMAL | 9 | 9 | 4 | N |

I test normalizzano a zero precisione e scala NULL del campo CHAR.

Per la tabella LOB:

```sql
SELECT COLUMN_NAME, DATA_TYPE, LENGTH, CCSID
FROM QSYS2.SYSCOLUMNS
WHERE TABLE_SCHEMA = 'PRO90DAT'
  AND TABLE_NAME = 'DB2ILOBT'
ORDER BY ORDINAL_POSITION;
```

Devono risultare `ID` VARCHAR di 32 caratteri, `BLOB_VALUE` BLOB,
`CLOB_VALUE` CLOB con CCSID 1208 e `DBCLOB_VALUE` DBCLOB con CCSID 1200.
La validazione del test richiede almeno 18 MiB per il BLOB e un valore LENGTH
di almeno 131072 per ciascuna colonna testuale LOB.

## 4. Abilitare il journaling su entrambe le tabelle

Commit, rollback e verifica del rollback al ritorno nel pool richiedono una
tabella journaled. Controllare prima se la creazione SQL lo ha già attivato:

```sql
SELECT *
FROM QSYS2.JOURNALED_OBJECTS
WHERE OBJECT_LIBRARY = 'PRO90DAT'
  AND OBJECT_NAME IN ('CLAUD00F', 'DB2ILOBT')
  AND OBJECT_TYPE = '*FILE';
```

Deve risultare una riga per ciascuna tabella. Se risultano entrambe, il
passaggio è completato. Quando la libreria è uno schema SQL con journal `QSQJRN`, la
creazione della tabella può attivare automaticamente il journaling. Vedere
la [documentazione IBM sul journaling SQL](https://www.ibm.com/docs/en/i/7.5.0?topic=integrity-journaling).

Per le tabelle non ancora journaled, usare il journal indicato dal responsabile
IBM i. Con un journal esistente, ad esempio `PRO90DAT/QSQJRN`, avviare:

```cl
STRJRNPF FILE(PRO90DAT/CLAUD00F) JRN(*LIBL/QSQJRN) IMAGES(*BOTH) OMTJRNE(*OPNCLO)
STRJRNPF FILE(PRO90DAT/DB2ILOBT) JRN(*LIBL/QSQJRN) IMAGES(*BOTH) OMTJRNE(*OPNCLO)
```

In alternativa, se si desidera un nuovo journal dedicato ai test, creare prima
receiver e journal. Eseguire i seguenti comandi solo quando questi nomi sono
disponibili; concordare con il responsabile IBM i la gestione dei receiver:

```cl
CRTJRNRCV JRNRCV(PRO90DAT/DB2ITRCV01) THRESHOLD(100000)
CRTJRN JRN(PRO90DAT/DB2ITJRN) JRNRCV(PRO90DAT/DB2ITRCV01) MNGRCV(*SYSTEM) DLTRCV(*NO)
STRJRNPF FILE(PRO90DAT/CLAUD00F) JRN(PRO90DAT/DB2ITJRN) IMAGES(*BOTH) OMTJRNE(*OPNCLO)
STRJRNPF FILE(PRO90DAT/DB2ILOBT) JRN(PRO90DAT/DB2ITJRN) IMAGES(*BOTH) OMTJRNE(*OPNCLO)
```

Il journaling delle tabelle si avvia con `STRJRNPF`, come descritto nella
[documentazione IBM](https://www.ibm.com/docs/en/i/7.6.0?topic=journaling-database-physical-files-tables).
Ripetere la query su `QSYS2.JOURNALED_OBJECTS` e verificare che restituisca
una riga per ciascuna tabella prima di eseguire i test transazionali e LOB.

## 5. Assegnare le autorizzazioni al profilo di test

Sostituire `UTENTETEST` con il profilo indicato nella connessione locale:

```sql
GRANT SELECT, INSERT, UPDATE, DELETE
ON PRO90DAT.CLAUD00F
TO UTENTETEST;

GRANT SELECT, INSERT, UPDATE, DELETE
ON PRO90DAT.DB2ILOBT
TO UTENTETEST;
```

Il profilo deve anche poter accedere alla libreria e interrogare
`QSYS2.SYSCOLUMNS` e `QSYS2.JOURNALED_OBJECTS`.

Se manca l'accesso alla libreria, un amministratore può assegnarlo con:

```cl
GRTOBJAUT OBJ(PRO90DAT) OBJTYPE(*LIB) USER(UTENTETEST) AUT(*USE)
```

## 6. Database host server e TLS

Per i test TCP il database host server deve essere attivo e raggiungibile
sulla porta 8471. Se deve essere avviato, il comando CL è:

```cl
STRHOSTSVR SERVER(*DATABASE)
```

Il comando richiede i sottosistemi IBM i previsti dalla configurazione del
server. Vedere [STRHOSTSVR](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fcl%2Fstrhostsvr.html).

Per eseguire anche i due casi TLS, preparare il database host server sicuro:

1. Aprire **Digital Certificate Manager (DCM)** e il certificate store `*SYSTEM`.
2. Creare o importare un certificato server valido per il nome DNS usato dalla
   connessione TLS.
3. Assegnarlo all'applicazione server **`QIBM_OS400_QZBS_SVR_DATABASE`**.
4. Rendere raggiungibile dal PC la porta TCP **9471**, usata dal database
   host server con TLS.
5. Se il certificato usa una CA interna, installare la CA nel trust store di
   Windows usato da .NET, con le eventuali CA intermedie.
6. Seguire la procedura dell'amministratore per rendere attiva l'assegnazione
   del certificato al servizio.

Riferimenti IBM:
[configurazione SSL](https://www.ibm.com/docs/en/ssw_ibm_i_76/rzahh/sslreq.htm) e
[certificati e porte dei database host server](https://www.ibm.com/support/pages/host-servers-requiring-digital-certificate-assigned-use-secure-socket-layer-ssl-ibm-i-access-client-solutions-odbc-driver).

La connessione TLS locale viene derivata da quella TCP, con `SSL=true`,
`Port=9471` e `Trust Server Certificate=false`. Se la connessione TCP usa un
indirizzo IP che non compare nel certificato, modificare **solo il Server
della connessione TLS** con il nome DNS coperto dal certificato. Il profilo e
la password restano quelli già configurati localmente.

## 7. Configurazione locale

Nel file `tests/Db2i.Tests/.env.local` mantenere la connessione TCP già
configurata e questi valori:

```dotenv
DB2I_TEST_DML_ENABLED=true
DB2I_TEST_DML_TABLE=PRO90DAT.CLAUD00F
DB2I_TEST_DML_ID_COLUMN=IDTBCL
DB2I_TEST_DML_DESCRIPTION_COLUMN=DESCCL
DB2I_TEST_DML_DATE_COLUMN=DT01CL
DB2I_TEST_DML_DECIMAL_COLUMN=DECICL
DB2I_TEST_LOB_ENABLED=true
DB2I_TEST_LOB_TABLE=PRO90DAT.DB2ILOBT
```

Il file contiene anche `DB2I_TEST_TCP_CONNECTION_STRING` e
`DB2I_TEST_TLS_CONNECTION_STRING`. L'abilitazione LOB richiede che la tabella
del punto 2 esista e che il journaling del punto 4 sia attivo. La presenza
della connessione TLS abilita i due casi TLS e richiede il completamento
del punto 6.

## 8. Caricare la configurazione ed eseguire i test

Le credenziali restano nel file locale, escluso da Git. `dotnet test` non
carica automaticamente il file: nella stessa sessione PowerShell da cui
avviare i test, eseguire:

```powershell
Set-Location C:\Sviluppo\ibm.db2.dotnet

Get-Content -LiteralPath 'tests/Db2i.Tests/.env.local' | ForEach-Object {
    if ($_ -match '^\s*(?:export\s+)?(DB2I_TEST_[A-Za-z0-9_]*)\s*=(.*)$') {
        $testVariableName = $Matches[1]
        $testVariableValue = $Matches[2].Trim()
        if ($testVariableValue.Length -ge 2 -and (
            ($testVariableValue.StartsWith('"') -and $testVariableValue.EndsWith('"')) -or
            ($testVariableValue.StartsWith("'") -and $testVariableValue.EndsWith("'"))
        )) {
            $testVariableValue = $testVariableValue.Substring(1, $testVariableValue.Length - 2)
        }
        [Environment]::SetEnvironmentVariable($testVariableName, $testVariableValue, 'Process')
    }
}

dotnet test Db2i.sln --configuration Release --filter 'Category=Integration' --logger 'console;verbosity=normal'
```

I test verificano la firma della tabella prima di scrivere, scelgono ID liberi
e ripuliscono le righe create. I test transazionali possono terminare senza
eseguire la verifica se il journaling non è attivo: controllare anche l'output
del test, non soltanto il conteggio dei casi superati.

Per eseguire soltanto i LOB, dopo aver caricato lo stesso file:

```powershell
dotnet test Db2i.sln -c Release --filter 'FullyQualifiedName~RealIbmILobIntegrationTests'
```

Con le due tabelle valide e journaled e le connessioni TCP/TLS configurate,
tutti i 16 casi di integrazione attuali hanno i prerequisiti per effettuare
le verifiche reali. Un caso riportato come superato può aver terminato senza
verifica se mancano le sue variabili; per questo devono essere presenti
entrambi i connection string, entrambi i flag di abilitazione e i nomi delle
tabelle. La suite non crea automaticamente strutture sul server.

# ADR 0001: usare il database host-server protocol

Data: 2026-07-24  
Stato: accettata

## Contesto

IBM i espone più percorsi di accesso al database. JTOpen contiene sia componenti
Toolbox sia codice relativo a DDM/DRDA. L'obiettivo di questo progetto è un
porting progressivo del percorso JDBC Toolbox.

## Decisione

L'MVP implementa il servizio `as-database` Client Access:

- server ID SQL `0xE004`;
- porta 8471 in chiaro;
- porta 9471 con TLS;
- header Client Access da 20 byte;
- richieste SQL native del database host server.

DRDA sulle porte 446/448 non fa parte dell'MVP.

L'API pubblica è basata su `System.Data.Common`; nessun dettaglio del protocollo
deve essere necessario per usare `Db2iConnection`, `Db2iCommand` e
`Db2iDataReader`.

## Conseguenze

- il comportamento può essere confrontato direttamente con JTOpen;
- non è necessario installare un driver nativo IBM sul client;
- autenticazione, CCSID e formati SQL devono essere implementati nel provider;
- il porting derivato resta soggetto a IBM Public License 1.0.

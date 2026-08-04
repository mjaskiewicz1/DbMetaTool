# DbMetaTool

Narzędzie CLI do zarządzania schematem bazy danych Firebird 5.0.  
Umożliwia budowanie nowej bazy na podstawie skryptów SQL, eksport metadanych oraz aktualizację istniejącej bazy.

## Wymagania

- .NET 8+
- Firebird 5.0 (serwer lokalny na porcie 3050)
- Domyślne dane logowania: `SYSDBA` / `password`

## Budowanie

```bash
dotnet publish -c Release -o C:\tools\DbMetaTool
cd C:\tools\DbMetaTool
```

## Użycie

### Budowanie nowej bazy danych

Tworzy plik `database.fdb` w podanym folderze na podstawie skryptów SQL.  
Folder ze skryptami musi zawierać pliki: `domains.sql`, `tables.sql`, `procedures.sql`.

```bash
.\DbMetaTool.exe build-db --db-dir "C:\db\fb5v3" --scripts-dir "C:\out"
```

### Eksport metadanych do skryptów SQL

Eksportuje domeny, tabele i procedury z istniejącej bazy do plików `.sql`.

```bash
.\DbMetaTool.exe export-scripts --connection-string "User=SYSDBA;Password=password;Database=C:\db\fb5v3\database.fdb;DataSource=localhost;Port=3050;Dialect=3;Charset=UTF8;ServerType=0" --output-dir "C:\out3"
```

### Aktualizacja istniejącej bazy danych

Dodaje brakujące domeny, tabele, kolumny i aktualizuje procedury.  
Przed aktualizacją tworzony jest backup w podkatalogu `backups\` obok pliku bazy.

```bash
.\DbMetaTool.exe update-db --connection-string "User=SYSDBA;Password=password;Database=C:\db\fb5v3\database.fdb;DataSource=localhost;Port=3050;Dialect=3;Charset=UTF8;ServerType=0" --scripts-dir "C:\out2"
```

## Znane ograniczenia

- **Usuwanie bazy po błędzie tworzenia nie zostało sprawdzone** — w przypadku błędu podczas `build-db` aplikacja próbuje usunąć niekompletny plik `database.fdb`, jednak ten mechanizm nie był testowany.
- **Przywracanie backupu po błędzie nie działa poprawnie** — w przypadku niepowodzenia `update-db` baza nie jest automatycznie przywracana do stanu sprzed aktualizacji. Backup jest tworzony i przechowywany w folderze `backups\`, jednak mechanizm automatycznego restore nie działa poprawnie — ręczne przywrócenie wymaga użycia narzędzia `gbak` lub Firebird Manager.
- **Częściowa obsługa constraints** — domeny obsługują `NOT NULL` oraz `CHECK`, natomiast `update-db` nie obsługuje dodawania ani modyfikacji kluczy obcych i unikalnych indeksów w tabelach.
- **Aktualizacja kolumn** — przed dodaniem kolumny sprawdzane jest czy tabela już istnieje w bazie; jeśli tak, dodawane są tylko brakujące kolumny.
- DDL w Firebird nie jest transakcyjne — rollback nie cofa zmian schematu, jedynym zabezpieczeniem jest backup.
- **Nieobsługiwane typy kolumn** — mogą wystąpić błędy związane z typami kolumn, ponieważ nie wszystkie typy Firebird są obsługiwane przez `GetFirebirdType`. W razie błędu baza zostanie przywrócona z backupu.

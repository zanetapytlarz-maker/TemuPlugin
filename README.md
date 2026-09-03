# Wtyczka TEMU do EasyUploader

Instrukcja przygotowania środowiska, aby wtyczka mogła pobierać zamówienia i generować etykiety przez API TEMU.

## Wymagania wstępne
- .NET Framework 4.8 (lub zgodna wersja, której używa EasyUploader do uruchamiania wtyczek).
- Dostęp do panelu TEMU oraz umowy partnerskiej zawierającej `ClientId`, `ClientSecret` i `ShopId`.
- Możliwość zapisu plików w katalogu instalacji EasyUploader (tam wtyczka tworzy podkatalogi `Orders`, `Labels` i `Logs`).

## Przygotowanie konfiguracji
1. Skopiuj pliki wtyczki (`TemuPlugin.dll` oraz zależności) do folderu wtyczek EasyUploader.
2. Uruchom wtyczkę lub stwórz ręcznie plik `temu.config.json` obok DLL wtyczki.
3. Jeżeli plik konfiguracyjny nie istnieje, wtyczka wygeneruje `temu.config.sample.json` – wypełnij pola i zapisz jako `temu.config.json`.
4. W pliku `temu.config.json` uzupełnij:
   - `BaseUrl` – zazwyczaj `https://api.partner.temu.com/` (ukośnik na końcu nie jest wymagany; wtyczka go doda).
   - `ClientId` – z umowy TEMU.
   - `ClientSecret` – z umowy TEMU.
   - `ShopId` – identyfikator sklepu TEMU.
   - `LabelFormat` – `PDF` lub `ZPL` zgodnie z potrzebami drukarki.

## Użycie w EasyUploader
- **Panel TEMU PL**: sprawdza, czy konfiguracja jest poprawnie załadowana i pokazuje aktualny `ShopId` oraz adres API.
- **Pobierz nowe zamówienia**: pobiera nowe zamówienia z TEMU i zapisuje je do `Orders/temu-orders-YYYYMMDD-HHMMSS.json`.
- **Pakuj (TEMU)**: odbiera XML z EasyUploader, wyciąga `order id`, pobiera etykietę z TEMU i zapisuje ją w `Labels` jako `PDF` lub `ZPL`.

## Logi i diagnostyka
- Błędy są zapisywane w `Logs/temu-plugin.log` (tworzony obok DLL wtyczki).
- Komunikaty o błędach z API TEMU zawierają kod statusu i treść odpowiedzi, co ułatwia zgłoszenie do wsparcia TEMU.

## Budowanie wtyczki samodzielnie
1. Otwórz rozwiązanie `TemuPlugin.sln` w Visual Studio (z platformą .NET Framework 4.8).
2. Zbuduj projekt `TemuPlugin.csproj`, upewniając się, że powstaje biblioteka `TemuPlugin.dll`.
3. Skopiuj wynikowe DLL do katalogu wtyczek EasyUploader.

Po wykonaniu powyższych kroków wtyczka jest gotowa do pracy – wymagane są jedynie prawidłowe dane z umowy TEMU w pliku konfiguracji.

## Jak pobrać gotową wtyczkę
Ten projekt nie zawiera binariów. Aby uzyskać wtyczkę:

1. Sklonuj repozytorium lub pobierz je jako ZIP.
2. Otwórz `TemuPlugin.sln` w Visual Studio i zbuduj konfigurację **Release** (platforma `Any CPU`).
3. Po zbudowaniu plik `TemuPlugin.dll` znajdziesz w `TemuPlugin\bin\Release`. Skopiuj go do katalogu wtyczek EasyUploader.
4. Obok DLL umieść plik `temu.config.json` (może powstać na bazie wygenerowanego `temu.config.sample.json`).

Po skopiowaniu tych plików uruchom EasyUploader, a wtyczka pojawi się w menu pod nazwą **TEMU PL**.

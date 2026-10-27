# Architektura

## Podzial odpowiedzialnosci

| Plik | Rola |
| --- | --- |
| `MainForm.cs` | Glowne okno, zakladki, ustawienia i wybor materialow |
| `DownloadQueueForm.cs` | Kolejka, limit rownoleglosci i podsumowanie |
| `BrowserDownloadForm.cs` | Odtwarzacz, przechwycenie MP4 i zapis z kontekstem sesji |
| `ActiveWebView.cs` | Host CoreWebView2Controller aktywny takze w ukrytej zakladce |
| `QualityPolicy.cs` | Rozpoznawanie wariantow jakosci i wybor najwyzszego |
| `Core.cs` | Modele, parser strony, HTTP, wznowienie i pomiar predkosci |
| `CaptchaDialog.cs` | Reczne wpisywanie CAPTCHA w dodatkowym trybie HTTP |

## Przeplyw

`MainForm` tworzy niemodalna kolejke osadzona w zakladce. Scheduler przygotowuje
odtwarzacze kolejno; gdy poprzedni zaczal zapisywac bajty, uruchamia nastepny,
do wybranego limitu. `parallel == 0` w konstruktorze kolejki oznacza wszystkie
wybrane pozycje. W GUI uzytkownik wybiera ten tryb checkboxem **Wszystkie**.

`OnResponse` akceptuje odpowiedzi MP4 o oczekiwanym typie i statusie 200/206
z dozwolonych serwerow. `SaveWithPlaybackContext` korzysta z naglowkow
oraz ciasteczek rzeczywistego odtwarzacza. Nie rozszerzaj przekazywania
tych danych na dowolne domeny.

Podstawowa sciezka zapisuje `.mp4.part` oraz metadane `.part.vider.xml`.
Wznowienie wymaga zgodnego zasobu i walidatorow HTTP. Sprawdzane sa
Content-Range, rozmiar i poczatek MP4. Po sukcesie zmieniana jest nazwa
na `.mp4`. Gotowy plik jest pomijany, a plik czesciowy innej instancji
chroniony. Zapasowy zapis WebView2 ma osobna nazwe `.browser-*.part`;
nie zapewnia identycznego mechanizmu wznowienia przez XML.

## Praca w tle

Wywolanie `PrepareAsync` jest jawne, poniewaz `Shown` moze byc odroczone
w ukrytej zakladce. `ActiveWebView` utrzymuje kontroler aktywny niezaleznie
od widocznosci UI. Po zmianie tego kodu sprawdzaj nie tylko trwanie transferu,
ale tez rozpoczecie kolejnego filmu przy zminimalizowanym oknie.
Operacje WebView2 wykonuj na watku GUI/STA.

## Jakosc

Polityka porownuje rozpoznane wymiary, a nastepnie bitrate, jesli jest dostepny.
Obsluguje JW Player oraz opisane elementy `source` HTML video. Czeka na
zakonczenie zmiany wariantu, zanim zapisze strumien. Gdy rozpoznany najwyzszy
wariant ma nieobslugiwany format, zapis zostaje zatrzymany. Nieznany odtwarzacz
nie daje gwarancji wykrycia wszystkich wariantow. Brak rozpoznania wariantow
nie stanowi scislej blokady zapisu przechwyconego MP4.

## Sprawdzanie zmian

- Buduj izolowana kopie; nie restartuj aktywnych pobran bez uzgodnienia.
- Testuj parser na lokalnym HTML i transfer na lokalnym serwerze testowym.
- Sprawdzaj zgodnosc bajtow, przerwane odpowiedzi i wznowienie.
- Dla rownoleglosci sprawdzaj nakladanie sie transferow, nie liczbe okien.
- Dla jakosci sprawdzaj zapis faktycznie wyzszego wariantu.
- Podstawowe uruchomienie pustego GUI nie wymaga kontaktu z Viderem.
- Zachowaj skladnie C# 5 dla kompilatora uzywanego przez `build.cmd`.

## Prywatnosc

W repozytorium nie przechowuj ustawien uzytkownika, logow, profili WebView2,
cookies, podpisanych adresow plikow, nagran ani zrzutow prywatnych stron.
Przykladowe dane powinny byc fikcyjne, a obraz dokumentacji pokazywac tylko UI.

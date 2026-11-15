# KissaKuva

Lataa uusimmat kissakuvat ja -videot Reddit-kanavista (alireddit). Ohjelma muistaa
jokaisen kanavan uusimman käsitellyn postauksen, joten seuraavalla ajolla se hakee vain
sen jälkeen tulleet postaukset. YOLO-tunnistus kertoo jokaisesta postauksesta, onko siinä kissa, ja muut voi halutessaan hylätä.

## Käyttö

```powershell
dotnet run --project src/KissaKuva                          # lukee kissakuva.json
dotnet run --project src/KissaKuva -- -c omat-kanavat.json  # toinen parametritiedosto
dotnet run --project src/KissaKuva -- --reject-non-cats --no-videos  # ohita tiedoston asetuksia
```

## Parametritiedosto

Kanavat ja asetukset ovat tiedostossa [kissakuva.json](kissakuva.json) (oletuksena nykyisestä
hakemistosta; toisen tiedoston voi antaa valitsimella `-c`). Tiedostossa saa olla kommentteja.

```jsonc
{
  "subreddits": [ "/r/aww", "/r/cats", "/r/catpictures" ],
  "outputDir": "kuvat",
  "statePath": "kissakuva-state.json",
  "limit": 50,
  "downloadVideos": true,
  "ffmpegPath": null,
  "rejectNonCats": false,
  "yoloModel": "models/yolov11s.onnx",
  "yoloClassModel": "models/yolov11s-cls.onnx",
  "confidence": 0.4,
  "classConfidence": 0.5,
  "keepRejected": false
}
```

| Kenttä | Merkitys | Oletus |
|---|---|---|
| `subreddits` | Kanavat joukkona. Muodot `/r/aww`, `r/aww` ja `aww` käyvät; sama kanava useaan kertaan (myös eri kirjainkoolla) käsitellään kerran. **Pakollinen.** | – |
| `outputDir` | Tallennushakemisto; tiedostot menevät alihakemistoon `<outputDir>/<kanava>/` | `kuvat` |
| `statePath` | Tilatiedosto, johon viimeisin haku tallennetaan | `kissakuva-state.json` |
| `limit` | Enintään näin monta postausta per kanava per ajo | `50` |
| `downloadVideos` | Lataa myös videot | `true` |
| `ffmpegPath` | ffmpeg-ohjelman polku; jos tyhjä, etsitään PATHista ja wingetin asennuskansiosta | – |
| `rejectNonCats` | Hylkää postaukset, joista ei tunnistettu kissaa (vaatii ffmpegin, jos videoita ladataan) | `false` |
| `yoloModel` | YOLO-tunnistusmalli (ONNX). Oletusmalli ladataan automaattisesti, jos sitä ei ole | `models/yolov11s.onnx` |
| `yoloClassModel` | YOLO-luokittelumalli (ONNX); tyhjä = ei luokittelua | `models/yolov11s-cls.onnx` |
| `confidence` | Tunnistusmallin vähimmäisvarmuus (0–1) | `0.4` |
| `classConfidence` | Luokittelumallin kissaeläinluokkien summan raja (0–1) | `0.5` |
| `keepRejected` | Tallenna hylätyt hakemistoon `<outputDir>/_ei-kissa/<kanava>/` | `false` |

Suhteelliset polut tulkitaan suhteessa parametritiedoston sijaintiin.

Komentorivin valitsimet ohittavat tiedoston asetukset: `-o/--out`, `-s/--state`, `-n/--limit`,
`--reject-non-cats`/`--no-reject`, `--model`, `--class-model`, `--confidence`, `--class-confidence`, `--keep-rejected`, `--no-videos`, `--ffmpeg`. Ohje: `--help`.

## Videot

Ladataan Redditin omat videot (`v.redd.it`), suorat `.mp4`-linkit, Imgurin `.gifv`-videot sekä
animoidut galleriakuvat. Tallennusmuoto on `.mp4`.

Redditin videoissa kuva ja ääni ovat erillisiä tiedostoja, jotka ohjelma yhdistää **ffmpegillä**
yhdeksi `.mp4`-tiedostoksi. ffmpeg haetaan PATHista, asetuksesta `ffmpegPath` tai wingetin
asennuskansiosta. Ilman ffmpegiä video tallennetaan ilman ääntä (ohjelma ilmoittaa tästä), ja
`rejectNonCats` vaatii sen aina, kun videoita ladataan. Asennus:

```powershell
winget install Gyan.FFmpeg
```

Ohjelma löytää wingetillä asennetun ffmpegin myös vanhasta päätteestä.

## Tila (viimeisin haku)

`kissakuva-state.json` sisältää kanavakohtaisesti uusimman käsitellyn postauksen
aikaleiman (`LastPostUtc`) ja viimeisimmän ajon ajan. Postaukset käsitellään vanhimmasta
uusimpaan ja tila tallennetaan postaus kerrallaan, joten keskeytetty ajo jatkuu seuraavalla
kerralla oikeasta kohdasta. Poista tiedosto (tai kanavan rivi), jos haluat aloittaa alusta.

## Reddit-yhteys

* **Ilman tunnuksia** ohjelma käyttää Redditin julkista RSS-syötettä. Se toimii heti, mutta
  Reddit rajoittaa sitä tiukasti (ohjelma odottaa automaattisesti), ja vain suorat kuva- ja
  videolinkit löytyvät; galleriat jäävät pois.
* **Suositeltu:** luo Reddit-sovellus osoitteessa <https://www.reddit.com/prefs/apps>
  (tyyppi *script*, redirect uri esim. `http://localhost:8080`) ja aseta ympäristömuuttujat:

  ```powershell
  $env:REDDIT_CLIENT_ID = "..."
  $env:REDDIT_CLIENT_SECRET = "..."
  ```

  Tällöin käytetään virallista OAuth-rajapintaa: nopeampi ja tukee myös galleriapostauksia
  sekä NSFW-suodatusta.

## Kissantunnistus (YOLO)

Kissaksi lasketaan kaikki kissaeläimet: kotikissat sekä tiikeri, leijona, leopardi, lumileopardi,
jaguaari, gepardi, ilves ja puuma. Tunnistus tehdään paikallisesti
[YoloDotNet](https://github.com/NickSwardh/YoloDotNet)-kirjastolla kahdella YOLO-mallilla (ONNX,
ajetaan CPU:lla). API-avaimia ei tarvita.

1. **Tunnistusmalli** (`yoloModel`, COCO-luokat) etsii kuvasta kohteita. Jos se löytää kissan
   vähintään `confidence`-varmuudella, kuva hyväksytään. Malli löytää myös pienet kissat isosta kuvasta.
2. **Luokittelumalli** (`yoloClassModel`, ImageNet-luokat) ajetaan vain, jos kissaa ei löytynyt. Se
   arvioi koko kuvan. Kissaeläinluokkien (tabby, Persian, Siamese, tiger, lion, snow leopard…)
   todennäköisyydet lasketaan yhteen, ja kuva hyväksytään, jos summa on vähintään `classConfidence`.

Oletusmallit `models/yolov11s.onnx` (38 Mt) ja `models/yolov11s-cls.onnx` (27 Mt) ladataan
automaattisesti ensimmäisellä ajolla YoloDotNetin GitHub-sivulta. Luokittelun voi kytkeä pois
asettamalla `yoloClassModel` tyhjäksi (`""`). Omat mallit pitää viedä ONNX-muotoon, esim.
`yolo export model=yolo11s.pt format=onnx opset=17`. Huomaa, että Ultralyticsin mallit ovat
AGPL-3.0-lisensoituja.

* **Kuvat** tunnistetaan sellaisenaan (GIF-kuvista ensimmäinen ruutu).
* **Videoista** tunnistetaan ensin postauksen esikatselukuva ja sitten enintään kahdeksan ruutua
  tasavälein (5 %:sta 92,5 %:iin kestosta). Tarkistus lopetetaan ensimmäiseen kissaan. Ruutujen poimiminen
  vaatii ffmpegin, joten `rejectNonCats`-asetus vaatii sen aina, kun videoita ladataan.
* **Galleriat** ratkaistaan kokonaisuutena: jos yhdessäkin kuvassa on kissa, koko galleria tallennetaan.

Tulos näytetään jokaisesta uudesta postauksesta yhdellä rivillä:

```
  kuva          kissa 93 %                                   tallennettu           Should I neuter my indoor cat?
  kuva          kissaeläin 61 % (egyptiläinen kissa, luokittelu) tallennettu       Blaze doesn't sit
  galleria (5)  kissa 3/5, paras kissa 92 %                  tallennettu           Molly
  video         ei kissaa (koira 92 %; luokittelu: Ibizan hound 50 %) hylätty    "Does your dog bite?"
```

Jos `rejectNonCats` on päällä, postaukset, joista ei löytynyt kissaa, hylätään: ne ohitetaan tai
tallennetaan `keepRejected`-asetuksella hakemistoon `_ei-kissa`. Muuten kaikki tallennetaan ja tulos
on vain tiedoksi. Pienempi raja löytää useampia kissoja mutta tekee myös enemmän vääriä tunnistuksia.

Mallit eivät tunnista kaikkea, joten `rejectNonCats` voi hylätä myös kissakuvia. Testeissä jäivät
tunnistamatta kissat kuljetuskopan kaltereiden takana, pelkkä kissan häntä ja lumileopardi.

## Rakenne

```
KissaKuva.sln
kissakuva.json        parametritiedosto (kanavat ja asetukset)
src/KissaKuva/
  Program.cs          pääohjelma
  Options.cs          parametritiedosto ja komentorivin valitsimet
  RedditClient.cs     Reddit-haku (OAuth/JSON tai RSS)
  VideoDownloader.cs  videoiden lataus (DASH + ffmpeg)
  StateStore.cs       tilan luku ja tallennus
  CatClassifier.cs    kissatunnistus (YoloDotNet)
  ImageFormat.cs      kuvamuodon tunnistus
```

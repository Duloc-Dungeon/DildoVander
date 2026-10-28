# Push oznámení

Oznámení se zapínají výslovně v účtu (`/me/account`, pro obsluhu `/settings`). Před zahájením večera je stejná karta v nabídce **Účet** na čekací obrazovce. Povolení se vztahuje na zařízení a aktuální přihlášení. Po novém přihlášení je potřeba znovu zapnout oznámení. Automatická obnova nativního tokenu zachovává existující souhlas; vypnutí má přednost před souběžnou obnovou. Karta nabízí i zkušební oznámení.

Server každých 15 sekund pozoruje uložený stav a vytváří zprávy pro:

- příchozí herní výzvu oprávněnému soupeři ve stejné spuštěné skupině;
- přiřazení existujícího hosta ke skupině a změnu jeho pozvánky;
- změnu plánu/odpočtu nebo otevření večera, včetně automatického začátku na konci odpočtu.

První registrace vytvoří výchozí stav, takže neposílá staré výzvy nebo pozvánky. Krátká událost, která skončí před dalším pozorováním, už oznámení nevyvolá. Nová pozvánka dosud nepřihlášenému hostovi zůstává odkazem: takový host ještě nemá registrované zařízení ani souhlas s push zprávami. Změna členství existujícího hosta tento problém nemá.

## Server

Funkce používá existující přihlašování `ApplicationIdentity`. Ve výchozím stavu je vypnutá. Konfiguraci nastav v soukromém prostředí služby, nikoli do veřejného repozitáře. V proměnných prostředí nahraď dvojtečky dvojitými podtržítky, například `Push__Enabled=true`.

| Klíč | Význam |
| --- | --- |
| `Push:Enabled` | Hlavní přepínač, výchozí `false` |
| `Push:Web:PublicKey` | Veřejný VAPID klíč, nekomprimovaný bod P-256 v base64url |
| `Push:Web:PrivateKey` | Odpovídající soukromý VAPID klíč v base64url |
| `Push:Web:Subject` | Kontaktní `mailto:` nebo HTTPS URL pro VAPID |
| `Push:Android:ProjectId` | Firebase projekt s povoleným FCM HTTP v1 API |
| `Push:Android:ServiceAccountPath` | Soukromý JSON service account s právem odesílat FCM zprávy |
| `Push:Ios:TeamId` | Apple Developer Team ID |
| `Push:Ios:KeyId` | Identifikátor APNs podpisového klíče |
| `Push:Ios:PrivateKeyPath` | Soukromý soubor APNs `.p8` |
| `Push:Ios:Topic` | Bundle ID aplikace, aktuálně `app.turquoise6409.green2444` |
| `Push:Ios:Sandbox` | `true` pro vývojové APNs, výchozí `false` pro distribuci |

Každá platforma se nabídne jen při vyplněné vlastní konfiguraci. Přítomnost hodnot neprokazuje platnost klíčů ani oprávnění poskytovatele. VAPID klíče vytvářej pomocí standardního VAPID generátoru, například `VapidHelper.GenerateVapidKeys()` z použité knihovny WebPush. Zachovej jejich pár mezi restarty; rotace vyžaduje nové přihlášení webového prohlížeče k oznámením.

### Kde získat údaje

**Web:** VAPID pár si vytvoříš lokálně; není potřeba Firebase ani účet Apple Developer. Jednorázový příkaz z oficiálního [web-push CLI](https://github.com/web-push-libs/web-push):

```sh
npx --yes --package=web-push web-push generate-vapid-keys --json
```

Výstup `publicKey` patří do `Push:Web:PublicKey`, `privateKey` do `Push:Web:PrivateKey`. `Push:Web:Subject` je kontaktní adresa, například `mailto:support@dildovander.de`. Soukromý klíč patří pouze serveru.

**Android:** Ve [Firebase Console](https://console.firebase.google.com/) vyber nebo vytvoř projekt a přidej Android aplikaci pro `de.dildovander.companion`. Její `google-services.json` je klientská konfigurace pro sestavení APK. Projektové ID najdeš v **Project settings → General**. Pro server povol **Firebase Cloud Messaging API (V1)** a v **Project settings → Service accounts** vygeneruj JSON klíč služby. Serverový service account musí mít oprávnění `cloudmessaging.messages.create`; vhodné omezené oprávnění lze nastavit přes IAM. Tento soubor ulož soukromě na server a odkaž na něj přes `Push:Android:ServiceAccountPath`.

Klientský `google-services.json` a serverový service-account JSON jsou dva různé soubory. Do `ANDROID_GOOGLE_SERVICES_JSON_BASE64` patří base64 celého klientského souboru. Secret se nastavuje v aplikačním GitHub repozitáři přes **Settings → Secrets and variables → Actions**. Serverový soukromý JSON nikdy nebal do aplikace. Viz [Firebase Android setup](https://firebase.google.com/docs/android/setup) a [FCM HTTP v1 authorization](https://firebase.google.com/docs/cloud-messaging/send/v1-api).

**iOS:** V [Apple Developer](https://developer.apple.com/account/) najdeš Team ID v **Membership details**. V **Certificates, Identifiers & Profiles → Keys** vytvoř klíč s oprávněním **Apple Push Notification service**, poznamenej Key ID a stáhni `.p8` (soukromý klíč lze stáhnout jen jednou). `.p8` patří soukromě na server; `Push:Ios:PrivateKeyPath` je jeho serverová cesta. U App ID `app.turquoise6409.green2444` zapni **Push Notifications** a vytvoř odpovídající provisioning profil. `Push:Ios:Topic` musí přesně odpovídat bundle ID. Podrobnosti: [vytvoření APNs klíče](https://developer.apple.com/help/account/keys/create-a-private-key/), [povolení capability](https://developer.apple.com/help/account/identifiers/enable-app-capabilities), [APNs prostředí](https://developer.apple.com/documentation/bundleresources/entitlements/aps-environment).

Po vložení údajů do soukromé runtime konfigurace nastav `Push__Enabled=true` a načti konfiguraci standardním provozním postupem. GitHub build secret sám o sobě server nenakonfiguruje. První kontrolu proveď přes **Poslat zkušební oznámení** po povolení oznámení v aplikaci; potom ověř příchozí herní výzvu i změnu večera.

Přihlášení, poslední pozorovaný stav a fronta jsou v `push.sqlite` pod `ApplicationIdentity:DataRoot`. Doručovací údaje i obsah zpráv chrání existující ASP.NET Data Protection. Zálohuj společně databázi a klíče z `ApplicationIdentity:DataProtectionRoot`. Není potřeba migrace databáze Identity. Odstranění účtu maže jeho push registrace i frontu; export účtu explicitně vylučuje doručovací přístupové údaje a interní frontu.

Fronta přežije restart. Pro jeden datový adresář provozuj jednu instanci backendu s push workerem; fronta nepoužívá distribuované pronájmy pro více odesílatelů. Neodeslané zprávy expirují za deset minut; dočasné chyby mají omezené opakování. Neplatné tokeny se odstraňují. Před odesláním se znovu ověřuje aktuální vlastník registrace, relace, stav účtu, přístup a aktuálnost události. Odhlášenému, zablokovanému nebo odebranému účtu se další zprávy neodesílají. Zprávu již přijatou poskytovatelem nelze odvolat a při pádu mezi odesláním a potvrzením může vzniknout duplicita. Zprávy obsahují stručný text a povolenou interní cestu, bez postav a rozhovorů.

## Web

Web Push vyžaduje HTTPS (při vývoji funguje loopback), podporu Push API a oprávnění uživatele. Manifest umožňuje instalaci webu. Na iOS/iPadOS je webové oznámení dostupné pro web přidaný na plochu. Worker `/push-sw.js` musí být dostupný jako JavaScript pod stejným originem; nemá fetch handler ani offline cache soukromých stránek. Manifest `/manifest.webmanifest` a ikona `/push-icon.png` jsou součástí Vite výstupu. CSP povoluje vlastní manifest a worker.

Webový profil v `web/deploy/site-locations.conf` má pro tyto tři soubory přesné veřejné GET/HEAD cesty s vlastními MIME typy a revalidací cache. Při aktivaci musí odpovídající profil používat i nasazený web; samotné vložení klíčů neodblokuje chybějící statickou cestu. Soukromá nasazovací autorita udržuje své kopie samostatně, proto jejich případnou aktualizaci proveď samostatně revidovaným provozním postupem.

Webové endpointy jsou omezené na podporované služby Chrome/FCM, Firefox, Apple a Windows. Odchozí transport odmítá neveřejné adresy a přesměrování. Přidání jiné push služby vyžaduje vědomé rozšíření validace a testů.

## Android a iOS

Android používá oficiální Capacitor Push Notifications a FCM token. Vytvoř Firebase Android aplikaci pro skutečné `applicationId` **`de.dildovander.companion`** a vlož její `google-services.json` do `mobilni-aplikace/android/app/`. Soubor je ignorovaný. V release workflow lze dodat secret `ANDROID_GOOGLE_SERVICES_JSON_BASE64`; dekóduje se jen pro sestavení a pak se odstraní. Bez souboru sestavení zůstává možné, ale karta neumožní zapnutí a klient nevolá Firebase registraci ani odregistraci. Automatická inicializace FCM a sběr Analytics jsou vypnuté; registraci spouští až souhlas uživatele. Android 13+ vyžaduje oprávnění k oznámením.

iOS vrací APNs token, nikoli FCM token. AppDelegate předává registraci pluginu; projekt obsahuje Push Notifications capability a `aps-environment` podle konfigurace (`development` pro Debug, `production` pro Release). Provisioning profil a App ID v Apple Developer musí push podporovat. Vývojový iOS build páruj s `Push:Ios:Sandbox=true`; distribuovaný build s `false`. Serverová konfigurace aktuálně volí jedno APNs prostředí pro všechny iOS registrace, proto nemíchej vývojové a distribuované buildy na jednom prostředí serveru.

Po instalaci npm závislostí a webovém buildu spusť z `mobilni-aplikace/`:

```sh
npx cap sync android
npx cap sync ios
```

Sestavení a podpis iOS vyžadují macOS/Xcode. Přijetí poskytovatelem ani skutečné zobrazení na telefonu neprokazují samotné unit testy nebo `cap sync`.

## HTTP rozhraní

Všechny cesty vyžadují aktuální účet a relaci. Změny používají existující kontrolu originu a `X-DV-CSRF`; není zde anonymní odesílání ani parametr příjemce.

| Metoda a cesta | Výsledek |
| --- | --- |
| `GET /app/v1/push` | `{ web, android, ios, publicKey, enabled }`, stav aktuální relace |
| `PUT /app/v1/push` | Registrace `{ platform: "web", endpoint, p256dh, auth }` nebo `{ platform: "android"/"ios", token }`; `204` |
| `DELETE /app/v1/push` | Odvolání všech push registrací aktuální relace i při vypnutém poskytovateli; `204` |
| `POST /app/v1/push/test` | Pevná zkušební zpráva jen vlastním registracím, `202` zařazeno do fronty |

Aktivní token jiné relace nelze převzít (`409 push_subscription_in_use`); odhlášená nebo expirovaná relace už převzetí neblokuje. `503 push_not_configured` znamená chybějící konfiguraci platformy, `400 invalid_push_subscription` chybný token/endpoint/klíče a `409 push_subscription_required` chybějící aktivní registraci pro zkoušku. `202` není potvrzení zobrazení na zařízení.

## Ověření

Backend harness má skupiny `--push` (offline stav, fronta a transportní hranice) a `--push-http` (skutečné HTTPS, účet, CSRF, origin a izolace relací), bez živých doručovacích služeb. Použij standardní restore/build postup z kořenového `AGENTS.md` a předej příslušnou skupinu sestavenému `BackendTests.dll`.

Z `mobilni-aplikace/`:

```sh
npx vitest run src/platform/pushNotifications.spec.ts src/platform/pushCoordinator.spec.ts src/features/identity/PushNotifications.spec.ts
node --test tests/push-worker.test.mjs
npm run build
npm run test:launch:browser
```

Pro opakovatelnou kontrolu povolení, zapnutí, vypnutí a zkušební zprávy spusť lokální Vite server na `127.0.0.1:5179` a `npm run test:push:browser`. Jiný origin lze předat přes `PUSH_BROWSER_URL`. Fixture používá syntetický účet a dvojníky Push API; nezapíná systémová oprávnění ani neposílá skutečné zprávy. Ověřuje rozměry 390×844 a 1280×900.

Před ostrou aktivací ověř skutečné doručení na každé nakonfigurované platformě v popředí, na pozadí i po zavření aplikace, otevření oznámení, odebrání oprávnění, vypnutí, odhlášení a změnu účtu. APNs sandbox a produkci testuj odděleně. Samotná implementace ani lokální test neprovádí nasazení či publikaci.

Referenční dokumentace: [Capacitor Push Notifications](https://capacitorjs.com/docs/apis/push-notifications), [Firebase Android setup](https://firebase.google.com/docs/android/setup), [Web Push na iOS/iPadOS](https://webkit.org/blog/13966/web-push-for-web-apps-on-ios-and-ipados/), [WebPush .NET](https://github.com/web-push-libs/web-push-csharp).

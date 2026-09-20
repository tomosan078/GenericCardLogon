# Apple Pay / エクスプレスカード対応

Apple Pay Suicaのエクスプレスカードは、一般的なPC/SCのGet UID (`FF CA 00 00 00`) だけでは8バイトのFeliCa IDmが得られない場合があります。iPhoneがISO/IEC 14443 Type Aとして応答し、4バイトのランダムNFCID1を返すケースがあるためです。

この版では、PC/SCだけでなく、SonyのFeliCa Library (`felica.dll`) を動的に利用して、Suicaのシステムコード `0x0003` をFeliCa Pollingします。8バイトIDmが取得できた場合のみ既存のSHA-256登録処理へ進みます。

SonyのFeliCa Libraryがインストールされていない場合は、従来のPC/SC方式へフォールバックします。

Apple Pay / Express Mode自体の設定はiPhone / Apple Wallet側で行います。Windows側から設定を変更することはありません。

## 依存

Sony NFC Port SoftwareをインストールしてRC-S380がWindowsから認識されていることが前提です。Managerは通常 `%ProgramFiles%\Common Files\Sony Shared\FeliCaLibrary\felica.dll` を探します。

## 注意

AppleはWindows PCアプリからApple Pay Express ModeのNFC応答方式を保証していません。この実装は、FeliCa PollingでSuica IDmを取得できる環境を対象とした実機検証用です。


## v9.5 Express timing robustness
- 0x0003 remains the primary Apple Pay Express system code.
- Polling now tries prescribed FeliCa time slots 0x03, 0x01, then 0x00.
- Physical FeliCa fallback uses 0xFFFF with 0x03, then 0x00.
- No raw Port-100 commands or undocumented Sony DLL entry points were added.
- Service keeps the warm Sony reader session and uses 24 Express rounds before wildcard fallback.

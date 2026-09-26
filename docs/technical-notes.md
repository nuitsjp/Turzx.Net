# Turzx.Net

Windows標準WinUSBでTURZX 9.2インチ（USB `1CBE:0092`）を制御する、非公式.NET 10ライブラリーです。NuGetパッケージIDは `Turzx.Net` です。現時点ではパッケージを公開していません。

## ライブラリーの利用

```csharp
using Turzx;

var devices = await TurzxDevices.ListAsync();
var device = devices.Single(); // 複数台ならアプリ側で選ぶ
await using var session = new TurzxSession(device.DeviceId);
session.StateChanged += (_, change) =>
    Console.WriteLine($"{change.OldState} -> {change.NewState}: {change.Error?.Message}");
await session.StartAsync();
await session.WaitUntilReadyAsync();
await session.SendFrameAsync(TurzxFrame.FromPng(portraitPngBytes));
```

入力は**462×1920に時計回りで回転済み**のBaseline JPEGまたはPNGです。1920×462の横長画像をアプリ側で描画してから回転・エンコードしてください。画像はSessionが保存・定期再送しません。

`TurzxDevices.ListAsync()` は現在接続中の対象を列挙します。`DeviceId` はUSBデバイスのインスタンス識別子から作り、Sessionは抜き差しをまたいで存続します。OSによるインターフェースの有効化を検知した後、WinUSBを開いて同期応答を確認すると `Ready` になります。`Disconnected` 中の画像送信は失敗し、再接続後はアプリが最新画像を送ります。送信中に切断された場合は表示されたか判断できないため、`TurzxDeliveryUnknownException` を返し、自動再送しません。同時送信は受け付けません。

接続処理の一時的な失敗は最大3回試み、失敗すると `Faulted` になります。`RetryAsync()` で接続処理を再開できます。`WaitUntilReadyAsync()` は `Ready` を待ち、`Faulted`・破棄・キャンセル時は例外で終了します。別ポートでの同一 `DeviceId` の維持、複数台、送信中の抜去は実機未検証です。

## miseタスクとリリース

Windowsのmiseから以下を実行できます。`.NET SDK 10.0.401` を `mise.toml` と `global.json` で指定しています。

| コマンド | 内容 |
| --- | --- |
| `mise run build` | ライブラリーと手動テスト用コンソールをビルド |
| `mise run test` | 接続中のTURZXを使う自動テストを実行 |
| `mise run pack` | プロジェクトに記載されたバージョンでローカルNuGetパッケージを作成 |
| `mise run manual` | 抜き差しを確認するコンソールを起動 |
| `mise run release -Preview` | 次のタグとパッケージバージョンを表示するだけ |
| `mise run release` | 自動採番してタグを作成し、`origin` へ送信 |
| `mise run release 0.2.0` | 指定バージョンのタグを作成し、`origin` へ送信 |

リリースタグは `vMAJOR.MINOR.PATCH` です。バージョン未指定時は、ローカルと `origin` にある最大のタグを数値比較し、PATCHを1増やします。まだタグがない場合はプロジェクトの `0.1.0` を基準に `v0.1.1` を提案します。明示したバージョンは既存の最大タグより大きい必要があります。

`release` は作業ツリーがクリーンであることを確認し、注釈付きタグを作成して `origin` に送信します。タグの送信を受けた [GitHub Actions](../.github/workflows/publish.yml) が、そのタグと同じバージョンでパッケージを作成し、NuGet.orgへ公開します。GitHubリポジトリのActions用Secret `NUGET_API_KEY` にNuGetのAPIキーを登録してください。キーはローカルの `release` には不要です。ワークフローにはUSB実機がないため、実機テストは実行しません。タグとNuGetパッケージの作成は、`release` を明示的に実行するまで行われません。

```powershell
dotnet test tests/Turzx.Net.Tests -c Release
dotnet run -c Release --project samples/Turzx.ManualTest
dotnet pack src/Turzx.Net -c Release -o artifacts/packages
```

自動テストはデバイスが接続された状態で、列挙、Sessionの初期化、JPEG／PNGの正常応答、画像入力の検証を行います。最後に送るJPEGには、見出し・日本語・7色の帯・四隅のラベルを描き、画面にそのまま残します。JPEGは実機表示を確認した32bit ARGB画像・品質85で生成します。USBの正常応答だけでは表示の正しさを証明できないため、必要に応じて目視確認してください。抜き差しの手動確認は [コンソールアプリの説明](../samples/Turzx.ManualTest/README.md) に従ってください。`dotnet pack` はローカルパッケージを作るだけで、NuGet.orgには公開しません。

## これまでの検証用コード

以下はライブラリー化前の通信・性能検証用コンソールコードです。
Windows標準WinUSBを直接使用し、Python・libusb・外部NuGetパッケージは不要です。

## 実機検証結果（2026-09-27）

**毎回描画・JPEG圧縮して約58 fpsを維持でき、CPU負荷はPC全体の約0.9%でした。**
fpsは機器の正常応答数から測った送信速度です。パネルの実表示fpsは未測定ですが、高速更新中も表示に問題がないことをユーザーが目視確認しました。

環境: Ryzen 9 7950X（16コア／32論理CPU）、96 GiB RAM、RTX 4060 Ti＋内蔵GPU、Windows 11、.NET SDK 10.0.401／Release。既存のWinUSBドライバーを使用しました。

| 条件 | 正常応答 fps | CPU平均（PC全体） | 測定時間 |
| --- | ---: | ---: | --- |
| JPEG・毎回描画／圧縮 | **58.10** | **0.89%** | 180秒 |
| JPEG・作成済み画像 | 58.21 | 0.03% | 180秒 |
| PNG・毎回描画／圧縮 | 18.51～19.10 | 0.63～0.71% | 20秒×2 |
| PNG・描画と送信を並行 | 23.95 | 0.86% | 20秒 |
| JPEG・圧縮しにくい乱数背景 | 25.37～26.10 | 0.65～0.70% | 20秒×2 |

JPEG品質85、同じ16パターンの文字・日本語・グラフを使用。全20試験で**32,714枚、通信エラー0件**でした。

最高速で毎回描画した180秒試験の負荷:

- **CPU:** 全体0.89%（論理CPU1個換算28.49%）。ユーザーモード0.80%、カーネルモード0.09%。
- **メモリ:** ピーク常駐量73.1 MiB。**GPU:** 対象PIDのGPUエンジン使用は検出されず、描画・圧縮はCPUで処理。
- **USB:** 画像3.18 MiB/s。1枚あたり描画1.98 ms、回転・圧縮2.68 ms、USB書き込み1.67 ms、応答待ち10.73 ms。
- **安定性:** 30秒ごとの速度は58.03～58.17 fps。描画開始から応答までの95パーセンタイルは17.75 ms。

JPEGの並行処理は速度を改善せず、逐次送信で十分でした。10 fpsに制限するとCPU約0.22%、1 fpsなら約0.03%です。静止画像は送信プログラム終了後も表示を維持します。
他アプリ稼働中の測定で、ドライバー全体のCPU負荷・電力・温度・H.264動画は未検証です。
測定条件、工程別の数値、OS負荷、制限事項は [詳細な検証記録](benchmark-2026-09-27.md) に記載しています。

## 送信形式（公開資料の調査）

画像はJPEG／PNGにエンコードしたバイト列を、512バイトの制御ヘッダーに続けて送信します。本実装では1920×462の画像を時計回りに90度回転し、462×1920として送ります。

| 形式 | 公開されているUSB実装 | 接続中の9.2インチでの確認 |
| --- | --- | --- |
| JPEG | コマンド101で画像送信 | 実機検証済み |
| PNG | コマンド102で画像送信 | 実機検証済み |
| H.264 / AVC | コマンド121で動画データを分割送信 | 実機未検証。本リポジトリでは未実装 |

動画の既存OSSはMP4からH.264をAnnex B形式で取り出して送信します。根拠は9.2インチを含む機種を扱う [OSSのUSB実装](https://github.com/mathoudebine/turing-smart-screen-python/blob/main/library/lcd/lcd_comm_turing_usb.py) と [動画対応のPR](https://github.com/mathoudebine/turing-smart-screen-python/pull/950) です。

メーカーの完全な対応形式一覧は見つかっていません。BMP・GIF・WebP・AVIF・非圧縮RGBの直接受信、JPEGのProgressive／色差サンプリング、PNGのビット深度／インターレース、H.264のProfile／Level／最大ビットレートなどの対応範囲は不明です。既存OSSはJPEGを `progressive=False` で生成していますが、これだけでは機器の制限までは断定できません。PCソフトが読み込める形式と、機器が直接受信できる形式は区別する必要があります。

## 接続状態・切断／再接続（実機検証済み）

**.NETから接続状態を取得し、USBの抜き差しをイベントで検出できます。** Windowsの `CM_Get_Device_Interface_ListW` で有効なインターフェースを列挙し、`CM_Register_Notification` で接続・切断通知を受信します。定期ポーリングや画面のメッセージループは不要です。

同じUSBポートで1回抜き差しした結果（2026-09-27、JST）:

| 確認項目 | 結果 |
| --- | --- |
| 開始時の接続状態 | 1台を検出し、WinUSBハンドル取得に成功 |
| 切断 | 05:14:33にREMOVAL通知。ハンドルを解放し、再列挙で0台 |
| 再接続 | 05:14:49にARRIVAL通知。新しいハンドルを取得し、再列挙で1台 |
| 再接続後の通信 | 別途 `show` を実行し、同期コマンドとPNG画像10枚すべて正常応答 |

通知登録後に初期状態を列挙し、パスで重複を除去します。通知コールバックはイベントをキューに渡し、ハンドル操作はコールバック外で行います。参照: [Microsoftの通知API仕様](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_register_notification)。

現在の検証コードはこのPCのWinUSBインターフェースGUIDと `1CBE:0092` を対象にした、1台・1回の抜き差し用です。接続状態はOSがインターフェースを有効と認識していることを示し、画像送信の成功とは別です。連続送信中の抜去、短時間の反復、スリープ復帰、別ポート、複数台、未接続からの起動は未検証です。描画処理への自動再接続はまだ組み込んでいません。物理的な抜き差しから通知までの遅延も未測定です。

検証ログ: `artifacts/connection-cycle.log`、`artifacts/connection-after-show.log`（Git管理対象外）。

## 実行

Windowsと.NET 10 SDK、WinUSB設定済みの機器が必要です。メーカーソフトは終了してください。

```powershell
dotnet run -c Release --project src/Turzx.Probe -- status
dotnet run -c Release --project src/Turzx.Probe -- watch-cycle 600
pnputil /enum-interfaces /enabled | Select-String 'VID_1CBE&PID_0092'
$devicePath = '\\?\USB#VID_1CBE&PID_0092#実機の識別文字列#{dee824ef-729b-4a0e-9c14-b7117d33a817}'
dotnet run -c Release --project src/Turzx.Probe -- show $devicePath
dotnet run -c Release --project src/Turzx.Probe -- benchmark $devicePath jpeg-live 180 artifacts/result.json
```

`status` は接続状態を表示します。`watch-cycle` は切断・再接続とハンドル再取得を1回確認すると終了し、指定秒数で完了しなければ失敗終了します。`show` はテスト画像を10回送信、`benchmark` は指定秒数の連続更新と計測を行います。再起動・ファームウェア更新・機器内ファイル変更・設定の永続保存は行いません。
詳細ログは `artifacts/benchmark/` に保存済みです（Git管理対象外）。[プロトコル参照元と帰属表示](../THIRD-PARTY-NOTICES.md)。

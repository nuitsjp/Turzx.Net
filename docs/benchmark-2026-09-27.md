# TURZX 9.2インチ 性能検証（2026-09-27）

TURZX 9.2インチ（1920×462、USB `1CBE:0092`）をC# / .NET 10から制御する技術検証。
Windows標準WinUSBを直接呼び出します。Python・libusb・外部NuGetパッケージは不要です。

## 検証結果（2026-09-27）

**このPCでは、毎回描画・JPEG圧縮しても約58 fpsで連続送信でき、CPU負荷はPC全体の約0.9%でした。**
ここでのfpsは「機器から正常応答を受けた画像数÷実経過時間」です。パネルが実際に表示した枚数は高速撮影していないため未測定です。高速更新中の画像に問題がないことはユーザーの目視で確認済みです。

環境: Ryzen 9 7950X（16コア／32論理CPU）、96 GiB RAM、RTX 4060 Ti＋内蔵GPU、Windows 11、.NET SDK 10.0.401／Releaseビルド。
デバイスは `TURZX1.0`、ドライバーは既存の `WINUSB / winusb.inf`。USB速度照会はHigh-speed以上、Bulk OUT `0x01`／IN `0x81`、最大パケット512バイトでした。

### 更新速度の比較

1920×462の同じ16パターン（文字・日本語・グラフ・移動マーカー）を使用。JPEG品質は85です。
「作成済み」は16枚を事前に圧縮、「毎回描画」は毎フレーム描画・回転・圧縮、「並行処理」は描画・圧縮を別スレッドでUSB送信と重ねています。

| 条件 | 正常応答 fps | CPU平均（PC全体） | 画像転送量 | 測定時間 |
| --- | ---: | ---: | ---: | --- |
| JPEG・毎回描画 | **58.10** | **0.89%** | 3.18 MiB/s | 180秒 |
| JPEG・作成済み | **58.21** | **0.03%** | 3.18 MiB/s | 180秒 |
| JPEG・並行処理 | 58.10 | 0.92% | 3.18 MiB/s | 20秒 |
| PNG・毎回描画 | 18.51～19.10 | 0.63～0.71% | 0.87～0.89 MiB/s | 20秒×2 |
| PNG・作成済み | 23.94～23.98 | 約0.01% | 1.12 MiB/s | 20秒×2 |
| PNG・並行処理 | 23.95 | 0.86% | 1.12 MiB/s | 20秒 |
| JPEG・乱数背景＋毎回描画 | 25.37～26.10 | 0.65～0.70% | 12.82～13.18 MiB/s | 20秒×2 |

小さいマーカーだけの圧縮しやすい画像も試し、JPEGは58.25 fps、PNGは26.33 fpsでした。
画像サイズは通常JPEG約56 KiB、通常PNG約48 KiB、乱数背景JPEG約517 KiBです。
JPEGの毎回描画・作成済みは事前の20秒×2試験でも各58.19～58.20／58.24～58.29 fpsでした。

### 最高速を維持したときの負荷

JPEG・毎回描画の180秒試験で、**10,458枚送信、エラー0件**。30秒ごとの速度も58.03～58.17 fpsでした。
作成済みJPEGの180秒試験は10,478枚、エラー0件です。全20試験の測定区間合計は約700秒・32,714枚で、通信エラーはありませんでした。

| 負荷／処理 | JPEGを毎回描画した180秒試験の実測 |
| --- | --- |
| CPU | 全体0.890%（論理CPU1個を100%とすると28.49%）。ユーザーモード0.802%、カーネルモード0.088% |
| CPUの短期変動 | 2秒ごとの観測で0.68～1.13% |
| メモリ | プロセスのピーク常駐量73.1 MiB。定常区間の観測は58.2～69.5 MiB、専用メモリ27.0～37.3 MiB |
| GPU | 88回の定常区間サンプルで対象PIDのGPUエンジン記録なし。画像生成・圧縮はCPUで実行 |
| USB | 画像3.18 MiB/s。コマンドと応答を加えると3.24 MiB/s（USBバス全体の占有率ではない） |
| 描画 | 平均1.98 ms/枚 |
| 回転＋JPEG圧縮 | 平均2.68 ms/枚 |
| コマンド作成 | 平均0.04 ms/枚 |
| USB書き込み／応答待ち | 平均1.67 ms／10.73 ms |
| 描画開始から正常応答まで | 中央値17.17 ms、95パーセンタイル17.75 ms、99パーセンタイル19.47 ms、最大37.16 ms |
| .NETメモリ確保／GC | 管理対象メモリ約10.0 MiB/s確保、180秒中のGC停止合計35.6 ms。GDI+のネイティブ確保量は含まない |

主なホスト負荷はCPUでの描画・圧縮です。作成済み画像ではCPUが約0.03%まで下がります。
通常画像ではUSB書き込みより応答待ちが長く、画像をさらに小さくしてもJPEGの速度は上がりませんでした。機器側処理／応答周期が上限の主因と考えられますが、内部のデコード時間と表示時間は分離できていません。
乱数背景ではUSB書き込みが約16 msに増え、内容によって速度が低下しました。

**この用途の最高速更新には、JPEGを逐次送信する構成が簡単です。** JPEGの並行処理は速度を改善せず、今回のキューでは生成開始から応答まで約52 msに延びました。PNGの並行処理は速度を改善しますが、同遅延は約125 msでした。
更新を10 fpsに抑えたJPEG試験ではCPU約0.22%、1 fpsでは約0.03%でした（各20秒）。静止画像は送信プログラム終了後も表示を維持します。長時間放置・再接続後の保持は未検証です。

### 測定方法と限界

- 初期化後5秒のウォームアップを除外し、測定区間全体でfpsとプロセスCPU時間を計算しました。CPUの「全体100%」は32論理CPU合計です。
- 初回のみ受信キューを読み切り、以降は空パケットを読み飛ばします。コマンド・正常応答コード・時刻の一致を全フレーム検証。1枚ずつ正常応答を待ち、異常時は停止します。
- 180秒試験では開始前20秒／実行中／終了後10秒を別プロセスで2秒ごとに監視しました。監視1回の所要時間は約1.17秒です。メモリのピーク値は起動・ウォームアップも含みます。
- 他アプリも稼働中です。毎回描画試験のPC全体CPUは開始前平均12.1%、定常区間12.2%でしたが、差を本プログラムの負荷とは断定できません。OS全体のディスク・ページングも記録しましたが、USB処理に帰属できません。
- SystemプロセスのCPU時間は権限不足で欠測。ドライバー全体の負荷は分離できていません。電力・温度・メモリ帯域・パネルの実表示fps・H.264動画は未測定です。
- 詳細な各フレームの時刻・工程時間、CPU・GCは `artifacts/benchmark/*.json`、集約は `summary.json`、OSカウンターは `monitor-live/` と `monitor-cached/` のJSON/CSVに保存しています（Git管理対象外）。

## 再実行

Windowsと.NET 10 SDK、WinUSB設定済みの対象機器が必要です。メーカーソフトは終了してください。

```powershell
pnputil /enum-interfaces /enabled | Select-String 'VID_1CBE&PID_0092'
$devicePath = '\\?\USB#VID_1CBE&PID_0092#実機の識別文字列#{dee824ef-729b-4a0e-9c14-b7117d33a817}'
dotnet build src/Turzx.Probe -c Release
$exe = (Resolve-Path 'src/Turzx.Probe/bin/Release/net10.0-windows/Turzx.Probe.exe').Path
& $exe probe $devicePath
& $exe show $devicePath
& $exe benchmark $devicePath jpeg-live 180 artifacts/benchmark/retest.json
```

`show` はテスト画像を10回送信し、現在の表示を置き換えます。`benchmark` は上表の `jpeg-live`、`jpeg-cached`、`jpeg-pipeline`、`png-live`、`png-cached`、`png-pipeline`、`jpeg-detail` のほか `jpeg-flat`、`png-flat` を指定でき、末尾に目標fpsを追加すると速度を制限できます。

OSカウンターも同時に採取する場合:

```powershell
.\tools\Measure-Probe.ps1 -ProgramPath $exe `
  -ProgramArguments @('benchmark', $devicePath, 'jpeg-live', '180', 'artifacts/benchmark/retest.json') `
  -OutputDirectory artifacts/benchmark/retest-monitor -SampleIntervalSeconds 2
```

送信画像は462×1920へ時計回りに90度回転したPNG/JPEGです。1 MiB超は停止します。再起動、ファームウェア更新、デバイス内ファイルの変更、設定の永続保存は実装していません。
プロトコルはMITの [turing-smart-screen-cli](https://github.com/phstudy/turing-smart-screen-cli/tree/ffc652ab7fa421ff57e7a2bf67c20bfccb884784) を参照しています。[帰属表示](../THIRD-PARTY-NOTICES.md)／[9.2インチ対応の根拠](https://github.com/mathoudebine/turing-smart-screen-python/discussions/980)。

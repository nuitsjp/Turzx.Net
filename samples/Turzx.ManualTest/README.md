# Turzx.ManualTest

TURZX の USB セッションを手動で切断・再接続し、再接続後に 1 枚だけ PNG フレームを送信する確認用コンソールです。

```powershell
dotnet run --project samples/Turzx.ManualTest
```

接続中の TURZX が 1 台なら自動選択されます。複数台ある場合は、一覧に表示された ID を任意引数で渡します。

```powershell
dotnet run --project samples/Turzx.ManualTest -- "<device-id>"
```

起動後に初期 Ready 状態を確認したら、指示に従ってデバイスを一度抜き、切断イベントを確認してから一度挿し直してください。再接続後、見出し・日本語・7色の帯・四隅のラベルを持つ1920×462の画面を、462×1920のPNGとして1回送信して終了します。

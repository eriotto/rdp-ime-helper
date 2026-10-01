# rdp-ime-helper

もうキーボード側でやるのやめた！

iPad（英語配列キーボード）から Windows App 経由で RDP 接続した日本語 Windows で、快適に日本語入力するための常駐ツールです。

- 左Alt単押しで IME オフ、右Alt単押しで IME オン（トグルではありません）
- Windows 側の配列は日本語（JIS）のまま、英語配列キーボードの刻印どおりに記号を入力できます（US→JIS 変換）
- 接続時に配列が日本語以外になっていたら、日本語（JIS）に戻します

## 導入方法

### winget（推奨）

接続先の Windows（RDP で操作する側）で実行します。

```powershell
winget install eriotto.RdpImeHelper
```

portable 形式なので、インストーラーは起動しません。exe が配置され、コマンド `RdpImeHelper` で起動できるようになります（新しいターミナルを開き直してください）。

```powershell
RdpImeHelper
```

### 手動

[Releases](https://github.com/eriotto/rdp-ime-helper/releases) から `RdpImeHelper.exe` をダウンロードし、ダブルクリックで起動します。.NET のインストールは不要です（self-contained 単一 exe）。

起動するとタスクトレイに常駐します。右クリックメニューから終了・ログの確認ができます。

### IgnoreRemoteKeyboardLayout の設定（推奨）

RDP クライアントのキーボード配列（英語）が接続先に持ち込まれないよう、レジストリの `IgnoreRemoteKeyboardLayout` を設定しておくと安定します。
未設定のときはトレイメニューに「IgnoreRemoteKeyboardLayout を設定する（管理者）」が表示されます。選ぶと UAC の確認が出て、書き込みます。設定は再接続（またはサインアウト）後に有効になります。

### 接続先を常に JIS 配列にする（推奨）

接続先の日本語配列は、既定（`KBDJPN.DLL`）では接続元が申告したキーボードの種類で 101/106 が決まり、最初に接続した端末の配列がその後も維持されます。
接続元に関係なく JIS に固定するには、管理者の PowerShell で次を実行し、**サインアウト**してから接続し直してください。

```powershell
# バックアップ
reg export "HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\00000411" "$env:USERPROFILE\Desktop\00000411-backup.reg"
# JIS（106/109）に固定
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\00000411" /v "Layout File" /t REG_SZ /d KBD106.DLL /f
```

元に戻すときは `KBD106.DLL` を `KBDJPN.DLL` にして実行します。PC 全体の設定なので、コンソール（PC の前で直接使う場合）も JIS になります。

US→JIS 変換は、接続元が英語配列キーボードのとき（iPad・Mac・英語配列の Windows など）だけ有効になります。接続元が JIS キーボードの場合は変換しません。

## アップデート

```powershell
winget upgrade eriotto.RdpImeHelper
```

> **常駐中は `winget upgrade` できません。**
> 起動中の exe はファイルがロックされるため、上書きに失敗します。先にトレイアイコンの右クリックメニューから「終了」してから実行してください。

PowerShell でまとめて行う場合：

```powershell
Stop-Process -Name RdpImeHelper -ErrorAction SilentlyContinue
winget upgrade eriotto.RdpImeHelper
Start-Process RdpImeHelper
```

## SmartScreen の警告が出る場合

`RdpImeHelper.exe` はコード署名をしていないため、ブラウザでダウンロードした exe を初めて起動すると「Windows によって PC が保護されました」と表示されることがあります。

1. 「詳細情報」をクリックします
2. 「実行」をクリックします

事前にブロックを解除しておくこともできます。

- エクスプローラーで exe を右クリック →「プロパティ」→「全般」タブ下部の「許可する」にチェック →「OK」
- または PowerShell で `Unblock-File .\RdpImeHelper.exe`

ダウンロードしたファイルが改ざんされていないかは、Releases に添付している `RdpImeHelper.exe.sha256` と照合できます。

```powershell
(Get-FileHash .\RdpImeHelper.exe -Algorithm SHA256).Hash
```

> キーボードフック（`WH_KEYBOARD_LL`）とキー入力の送信（`SendInput`）を使うため、セキュリティソフトによっては警告されることがあります。

## ログ

`%LOCALAPPDATA%\RdpImeHelper\RdpImeHelper.log`（トレイメニュー「ログを開く」）

## リリース手順（メンテナー向け）

1. `vX.Y.Z` 形式のタグを push します
   ```sh
   git tag v0.1.0
   git push origin v0.1.0
   ```
2. `release` ワークフローが、テスト → self-contained 単一 exe のビルド → GitHub Releases へのアップロード（exe と SHA256）を行います
3. 続けて `winget` ワークフローが、[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) にマニフェストの PR を出します
   - 既に登録済みなら `wingetcreate update` で新バージョンの更新 PR
   - 未登録（初回）なら `winget/templates/` から portable 形式のマニフェストを生成し、`wingetcreate submit` で提出
   - 生成したマニフェストはワークフローの成果物（`winget-manifests-X.Y.Z`）にも保存されます

事前準備として、リポジトリのシークレット `WINGET_TOKEN` に、`public_repo` スコープの classic Personal Access Token を登録してください（winget-pkgs の fork と PR 作成に使います。`GITHUB_TOKEN` では権限が足りません）。未登録の場合、PR は出さずに成果物の生成だけ行います。

マニフェストの生成だけを試す、または過去のタグで出し直すときは、Actions の `winget` ワークフローを手動実行（`tag` を指定、`dry_run` で PR なし）します。

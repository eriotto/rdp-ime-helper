# RdpImeHelper

iPad（英語配列キーボード）から Windows App 経由で RDP 接続した日本語 Windows で、
快適に日本語入力するための常駐ツール。

## 目的
- 左Alt単押し → IMEオフ、右Alt単押し → IMEオン（トグルではない）
- 英語配列の刻印どおりに記号を入力できるよう、US→JIS変換を行う
- Windows側のキーボード配列は日本語（JIS）固定が前提

## 技術スタック
- .NET 8 / C# / WinForms（タスクトレイ常駐）
- P/Invoke は CsWin32（NativeMethods.txt）を使う
- 配布：self-contained 単一exe、winget（portable）
- テスト：xUnit（ロジック部分のみ）
- 開発環境はLinuxコンテナ。ビルドは EnableWindowsTargeting=true で通すこと

## 構成
- KeyProcessor：純粋ロジック。Win32非依存。Alt単押し判定の状態機械とUS→JIS変換テーブル。
  入力（スキャンコード, keydown/up, 修飾状態, 判定結果フラグ）→ 出力（送信するアクション列）
- KeyboardHook：WH_KEYBOARD_LL の登録・解除。LLKHF_INJECTED 付きは無視
- InputSender：SendInput（KEYEVENTF_SCANCODE）ラッパー。Shift状態の一時調整
- ImeController：ImmGetDefaultIMEWnd + WM_IME_CONTROL(0x283)/IMC_SETOPENSTATUS(0x6)
- LayoutMonitor：配列判定・是正
- TrayApp：NotifyIcon、メニュー、状態表示

## 仕様
### Alt単押し
- Alt押下〜離しの間に他のキーが挟まらなければ単押し
- 長押し・組み合わせ時は通常のAltとして動作
- 離す直前にダミーキー（VK 0xE8）を送ってメニューバーのアクティブ化を抑止
- 配列に関係なく常に有効
- Alt押下前からCtrl/Shift/Winが押されている場合は単押し扱いしない（Shift+Alt、Ctrl+Altなど）
- Alt押下中にマウスボタンが押されたら単押しを取り消す（WH_MOUSE_LLで監視。Alt+クリック対策）
- 押下時間が500msを超えたら単押し扱いしない

### US→JIS変換
- 有効条件：RDPセッション（GetSystemMetrics(SM_REMOTESESSION)）かつ 現在の配列がJIS
- Unicode送信は禁止（IMEを素通りするため）。必ずJIS側のスキャンコードで送る
- Shift状態が異なる場合は Shift離す→キー→Shift押し直し
- キーリピートに対応
- 修飾キー
  - 変換するのは「修飾なし」または「Shiftのみ」のときだけ
  - Ctrl/Alt/Winのいずれかが押されている間は変換しない（ショートカットはVKで解釈されるため。例：Ctrl+=のズーム、Ctrl+[）
  - 修飾キー自体のイベントは素通しする
  - Shiftの状態は左右別に、非injectedイベントから自前で追跡する（GetAsyncKeyStateに頼らない）
  - Shiftを一時的に離した・押した場合は、送信後に物理状態へ正確に戻す（左右の区別も維持）
  - 変換の途中でShiftが離されるなど状態が食い違った場合は、物理状態を正として復元する（Shiftの押しっぱなし事故を防ぐ）
  - CapsLock（0x3A）はJISでは英数キーとして扱われる。単押しはShift+0x3A（CapsLock）に変換する（挙動は実機で要確認）
- 変換表（US入力 → JISで送るキー）
  - Shift+2 @ → @キー(0x1A) Shiftなし
  - Shift+6 ^ → ^キー(0x0D) Shiftなし
  - Shift+7 & → Shift+6(0x07)
  - Shift+8 * → Shift+:キー(0x28)
  - Shift+9 ( → Shift+8(0x09)
  - Shift+0 ) → Shift+9(0x0A)
  - Shift+- _ → Shift+ろキー(0x73)
  - = → Shift+-(0x0C)
  - Shift+= + → Shift+;キー(0x27)
  - [ { → [キー(0x1B)（Shiftは入力のまま）
  - ] } → ]キー(0x2B)（Shiftは入力のまま）
  - \ → ろキー(0x73)
  - Shift+\ | → Shift+¥キー(0x7D)
  - Shift+; : → :キー(0x28) Shiftなし
  - ' → Shift+7(0x08)
  - Shift+' " → Shift+2(0x03)
  - ` → Shift+@キー(0x1A)
  - Shift+` ~ → Shift+^キー(0x0D)
  - 注意：USの ` はJISの半角/全角(0x29)と同位置。必ず横取りする

### 配列判定
- キー押下ごとに前面ウィンドウのスレッドのHKLを取得
- MapVirtualKeyEx でスキャンコード0x1Aの文字が '@' ならJIS、'[' ならUS
- ToUnicodeEx は使わない（デッドキー状態を壊す）
- 結果は Dictionary<HKL,bool> でキャッシュ

### 配列の是正
- 契機：WTSRegisterSessionNotification の接続/再接続、および判定でJISでなくなったとき
- パターンA（HKLが日本語以外）：LoadKeyboardLayout("00000411") + WM_INPUTLANGCHANGEREQUEST で戻す
- パターンB（HKLは日本語だが101配列の挙動）：実行時是正はしない。トレイ通知のみ
- 起動時に HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layout\IgnoreRemoteKeyboardLayout を確認。
  未設定ならトレイメニューに「設定する」を表示し、自身を runas で昇格起動して書き込む
- 是正は接続1回につき1回まで。失敗時は通知のみ
- 是正はUIスレッドへ PostMessage で依頼。フックのコールバック内で行わない

### 常駐・起動
- exe単体のダブルクリックで起動できること
- 名前付きMutexで二重起動防止
- トレイメニュー：有効/無効、ローカルでも強制有効（テスト用）、ログオン時に起動、ログを開く、終了
- 自動起動はタスクスケジューラ登録。パスはバージョンを含まない安定パス（wingetのエイリアス）
- ツールチップ：「RDP：○ ／ 配列：JIS ／ 変換：ON」。更新は1秒タイマー

## 制約
- フックのコールバックは軽く保つ。I/O・ログ書き込み・重い処理は禁止（キューに積んで別スレッドで処理）
- ログ：HKL、配列判定結果、送信したスキャンコード、是正の実行結果を出力
- 変換表・設定はコード内に直書き（設定ファイルは作らない）
- 過剰な抽象化をしない

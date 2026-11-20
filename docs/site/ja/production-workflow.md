---
title: 制作からゲーム組み込みまで：Unity・Unreal・Godot
---

# 制作からゲーム組み込みまで：Unity・Unreal・Godot

ここで扱うのはノードの Flow ウィンドウではなく、**開発・制作の進め方**です。

**SDK 導入 → LIBERA FX 起動 → エンジン資源の取得・割り当て →
制作とプレビュー転送 → 書き出し → エンジン用アセット化 →
LIBERA FX なしで再生 → 一括変換・ゲームビルド**の順に進めます。

[Unreal](#unreal)・[Unity](#unity)・[Godot](#godot) の手順を選んでください。
[一括コンバーター](batch-conversion.md)、[SDK 導入の詳細](engine-integration.md)、
[Preview の更新手順](preview-update.md)も参照できます。

## ツール間で何を受け渡すのか

| データ | 役割 |
| --- | --- |
| `.lfxproj` | 編集用ソースと履歴。バージョン管理しますが、ゲーム用の再生データではありません。 |
| エンジンの Mesh・Material・Texture | エンジンのプロジェクト内に保持します。資源取得は参照・メタデータの取得であり、エンジンの実体アセットを LIBERA FX に複製することではありません。 |
| ライブプレビュー | 制作中だけ使う、認証付きの一時接続。保存済みのゲーム用アセットではありません。 |
| `.lfxb` | 通常再生用のコンパイル済みエフェクト。対応する CPU／GPU データを含みますが、エンジン資源ファイルそのものをすべて内包するわけではありません。 |
| エンジン用アセット・割り当て | Unity のインポート済みアセットと参照、Unreal の `.uasset` と cook 対象の参照、Godot の資源と明示的バインド。 |

### 現在のエンジン間の違い

| 操作 | Unreal | Unity | Godot |
| --- | --- | --- | --- |
| 「コンテンツ」からエンジン資源を一覧検索 | 対応 | 全資源カタログは未対応 | 全資源カタログは未対応 |
| Resource Identity 経由の厳密な資源・プロパティ照会 | 対応 | 対応 | デスクトップの ID 対応付けは未対応 |
| デスクトップからのプレビュー | Unreal 専用プレビュー画面 | 明示的に有効化する Unity 専用プレビュー画面 | スクリプトで専用 Player に接続 |
| ゲーム内での通常再生 | `.lfxb` をインポート | `.lfxb` をインポートし Unity 資源を割り当て | `.lfxb` を読み込み資源を登録 |

**共通の Resource Identity 対応付けは、まだ通常配布まで一貫した出力経路ではありません。**
Unreal／Unity の検証付きライブ転送には使えますが、登録済み ID を持つ文書の
通常の **Export VFX...**／コンバーター `.lfxb` 出力は、未解決 ID の破棄を防ぐため拒否します。
`.lfxrm` は診断用マニフェストで、**エンジンへそのままインポートする再生アセットではありません**。
エラーを消すために対応付けを削除せず、現行の通常配布には意図的に管理した
従来キー方式のプロジェクトを使ってください。下記 Unity の ID 登録手順にもこの制限があります。
[資源 ID の仕様](https://github.com/Libra-Works/LibraFX/blob/main/docs/resource-identity.txt)に詳細があります。

この工程差の解消と、ID 対応付けから通常配布までの完結は
[issue #71](https://github.com/Libra-Works/LibraFX/issues/71)で追跡します。

<span id="unreal"></span>

## Unreal Engine

### 1. エンジンへインストール

Unreal を終了し、同じビルド番号の `LibraForUnreal` を展開します。
`LibraRuntimeForUnreal` を `<Project>\Plugins\LibraRuntimeForUnreal` にコピーし、
プロジェクトの **Editor target** を再ビルドして起動します。
全バージョン共通のビルド済みプラグインではなく、ソースプラグインです。
[導入・検証環境の詳細](engine-integration.md)も確認してください。

### 2. LIBERA FX を起動して接続

`LiberaFx.exe` を起動し、**File → New / Open** で文書を作成・読込します。
チームの制作フォルダーへ `.lfxproj` を保存します。
Unreal で **Window → LIBERA FX Preview → Connect to Libra** を選びます。

Windows では有効なローカル接続先が1つなら接続し、複数なら **Select Libra** で明示的に選びます。
手動接続は LIBERA FX の **View → Preview connections** から
**Copy manual connection information** を実行し、Unreal の **Paste Connection** へ渡します。
一時招待情報は公開・保存しないでください。LIBERA FX を再起動したら再接続が必要です。

### 3. エンジンからリソースを取得

LIBERA FX の **View → Content（コンテンツ）→ 接続中エンジン**を開きます。
対象の Unreal 接続を選び、名前・種類で検索し、アセットを選択してサムネイルと
マテリアル情報を取得します。対応する **モデル追加／スプライト追加**を使うか、
グラフの対象ノードを選んで**割り当て先 → 割り当て**を実行します。
詳細取得を待ち、候補が複数なら正しいスロット・Texture 資源を選んでください。
割り当ては Undo／Redo の対象です。

`/Game`・`/Engine` の参照は Unreal 側で解決します。
別プロジェクトの同名ファイルを自動的に同じ資源とは扱いません。
ローカル PNG／JPEG のプレビューも、エンジンにインポート済みの Texture ではありません。
Resource Identity 登録は別の任意操作で、前述の通常出力の制限があります。

### 4. 制作してプレビュー転送

ノード・接続・値・タイムラインを編集します。
接続中の Unreal 専用シーンへコンパイル済みデータが転送され、実エンジンで描画されます。
ゲーム用レベルを直接書き換える操作ではありません。
**Preview connections** と **View → Log** でコンパイル・資源解決・シェーダーのエラーを確認し、
接続表示だけでなく Unreal 側の動きと実画面を確認します。

GPU は [GPU パーティクル](gpu-particles.md)／[2D の煙・インク](fluid-2d.md)も参照してください。
Unreal の GPU プレビューは前方シーク未対応で、後方シークは任意時刻の復元ではなく
シミュレーションをゼロへリセットします。コンパイル失敗は転送成功ではありません。

### 5. エンジン用アセットを作成しゲームで再生

通常出力できる文書を保存し、**File → Export VFX...** で `.lfxb` を書き出します。
Unreal の **Content Browser** でインポートし、アセットをレベルへドラッグします。
公開窓口の **Libra Effect** Component を持つ Actor が作成されます。
**Auto Play** を有効にして PIE で確認するか、Blueprint／C++ から `Play()` を呼びます。
グラフの通知は `OnGameIoEvent` で受け取れます。

インポート済みアセットの **LIBERA FX Resource Overrides** では、配信されたキーに対して
プロジェクト内の Static Mesh・Texture2D・Material を選べます。
`.uasset` を保存すると cook 対象の参照として保持されます。
ソースグラフの変更ではなく、Component 側の個別割り当てがあればそちらが優先です。

編集元を開く導線には、アセットのエディター専用 **Source Project Path** に `.lfxproj` を設定し、
**Open LIBERA FX** を使います。`.lfxb` の再インポート元とは別です。
Windows は `.lfxproj` の関連付け、または Unreal 起動前の
`LIBERAFX_EDITOR_EXECUTABLE` の設定が必要です。
LIBERA FX が起動済みの場合は既存画面からソースを開いてください。起動引数は転送されません。

### 6. 更新と配布

同じ `.lfxb` を再出力し、Unreal で再インポートして再生し直します。
資源キーを変更した場合は割り当ても確認してください。新しいキーへの推測移行は行いません。
LIBERA FX を切断した状態でも Play・自然終了・Stop・再生し直しを確認します。
インポート済みエフェクトと参照資源を cook し、配布先に元の `.lfxproj` や制作 PC のパスを要求しません。
CPU／GPU・Fluid2Dの出力は[一括コンバーター](batch-conversion.md)で自動化し、
その後はプロジェクトの通常の Unreal インポート・cook 手順を実行します。

<span id="unity"></span>

## Unity

### 1. エンジンへインストール

Unity を終了し、`LibraForUnity` の `works.libra.liberafx` を
`<Project>\Packages\works.libra.liberafx` へコピーします。
配布用フォルダーには Windows x64 native bridge が同梱されています。
`Assets` に同じプラグインを重複導入しないでください。
Unity を開き直し、パッケージ・native 読込エラーを先に解消します。
他 OS／CPU には対応する native binary が必要です。[導入の詳細](engine-integration.md)も参照してください。

### 2. LIBERA FX を起動して接続

LIBERA FX を起動して `.lfxproj` を保存します。
Unity の **Tools → LiberaFx → Resources → Connect to LIBERA FX** を開きます。
LIBERA FX の **View → Preview connections → Copy manual connection information** で取得した
現在の接続情報から、Unity の入力欄へ以下を転記します。

| Unity の入力欄 | 渡す内容 |
| --- | --- |
| WSS endpoint | 招待の host・port を使う `wss://<host>:<port>/preview?protocol=3` |
| Preview token | `accessToken` |
| TLS SHA-256 fingerprint | `certificateFingerprint` |

描画する場合は **Connect の前に Render desktop preview in Unity を有効化**し、接続します。
資源照会だけの接続では描画されません。
これは Preview 用で、MCP・デバッガーの認証情報とは別です。
招待情報をアセット・スクリプト・ログ・リポジトリに保存しないでください。

### 3. Unity の資源情報を取得して明示的に割り当て

Unity 全資源の一覧検索は Unreal と同じ方式では提供していません。
ID 対応付けを使うライブ制作は次の手順です。

1. Unity で **Tools → LiberaFx → Resources → Initialize or Resolve Project Identity**。
   複製プロジェクトの場合は共有 ID の維持／別 ID への分岐を意図的に選びます。
2. 保存済み Mesh・Texture2D・Material を選び、同メニューの **Copy Selected Asset Binding**。
3. LIBERA FX の **View → Content → Resource identity** で Unity 接続を選び、
   **Read project identity**。対象ノード・資源位置を選び、GUID／local-file-ID を貼り付け、
   **Query this exact binding** で照会します。
4. 成功後に **Register and assign selected location**。
   マテリアルは実スキーマを読み、プロパティも明示的に対応付けます。名前から推測しません。

これは ID 対応付きライブ転送であり、**現時点ではそのまま `.lfxb` 出力できる経路ではありません**。
通常配布の従来キー方式では、グラフ／パッケージのキーに対して、手順5で Unity の実体を明示的に割り当てます。

### 4. 制作してプレビュー転送

接続前に描画を有効化した場合、コンパイル済みパッケージと確認済みの対応付けが
Unity ウィンドウ内の専用プレビューへ送られます。
タイムライン変更も反映されますが、後方シークは再生成・再実行で、範囲は0～120秒です。
ゲーム用シーンそのものではありません。ウィンドウの診断と実際の描画を確認します。

保存する Unity ランタイム割り当てが必要なら、Resource identity の
**Deliver Unity runtime bindings** を明示的に実行します。
`Assets\LiberaFx\LiberaFxBindings.asset` を作成・更新します。
プレビュー用割り当ては一時的であり、この配送だけで通常パッケージが出力されたり、
ID 対応付けの出力制限が解消したりするわけではありません。
この WSS 経路は SOURCE 骨格入力・手動 Editor Send・GPU 資源対応付けには未対応で、
Live Game 接続とは別です。

### 5. エンジン用アセットを作成しゲームで再生

通常出力できる従来キー方式の文書を **File → Export VFX...** で `.lfxb` にします。
Unity の `Assets` 以下へ置くと `LiberaFxSpawnableAsset` としてインポートされます。
新しいパッケージを `.bytes` に改名しないでください。

**Assets → Create → LiberaFx → Unity Bindings** で割り当てアセットを作ります。
**Render Assets** の正確な `assetIdentity` に Mesh／Texture と必要な既定 Material を割り当て、
必要に応じて **Materials** とシェーダープロパティの明示的対応付けを設定します。
キーは単なる参照識別子であり、Unity のパスを名前から推測する仕組みではありません。

GameObject に `LiberaFxSpawnableComponent` を追加し、**Effect** と **Engine Bindings** を指定します。
**Play On Enable** を有効にして Play Mode、またはコードから `Play()` を呼びます。
`LastError` と `GameIoEventReceived` で失敗やグラフ通知を確認できます。
GPU は [GPU ガイド](gpu-particles.md)のアーティファクト・割り当ても必要です。

### 6. 更新と配布

`.lfxb` を再出力し、Unity の再インポート完了後に再生し直します。
変更していない `.meta` とシリアライズ済み参照を保持してください。
デスクトップ接続なしで読込・Play・自然終了・Stop・再生し直しを確認します。
ゲームの通常ビルドにインポート済みアセット・参照資源・ターゲットに合う native plugin を含めます。
[一括コンバーター](batch-conversion.md)は実行用エフェクトパッケージを生成しますが、
Unity シーン・割り当てアセット・Player ビルドは生成しません。

<span id="godot"></span>

## Godot

### 1. エンジンへインストール

Godot を終了し、`LibraForGodot` の `liberafx` を `<Project>\addons\liberafx` へコピーします。
`.gdextension` と `bin` をセットで維持してください。
同梱版は **Godot 4.7／Windows x64 用**で、他環境は対応するビルドが必要です。
開き直して `LibraEffectPlayer` が利用できることを確認します。
これは GDExtension であり、接続ツールバーを持つ EditorPlugin ではありません。

### 2. LIBERA FX 起動とシーンの準備

LIBERA FX で `.lfxproj` を開いて保存し、Godot には `LibraEffectPlayer`、
Camera3D、必要な照明・環境を持つ検証シーンを用意します。
ライブプレビューは**停止中の専用 Player**を使い、ゲーム再生中の Player と兼用しません。
Player と接続用 Node の両方をシーンツリーへ入れてから接続します。

### 3. Godot 資源を明示的にバインド

Godot の全資源カタログ取得・デスクトップの Resource Identity 対応付けは未対応です。
停止中の Player に Mesh／Texture2D／Material を登録します。

```gdscript
$LibraEffectPlayer.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", BoxMesh.new())
$LibraEffectPlayer.set_texture_binding("/Game/FX/T_Atlas.T_Atlas", preload("res://atlas.png"))
```

文字列は**グラフ側の参照キーの例**であり、Godot のファイルパスでも
Unreal から自動読込する指定でもありません。エフェクト内の正確な識別子に合わせます。
Material は `set_material_binding(identity, material)` を使います。
未登録の資源は代用品に置換せず、再生を拒否します。

### 4. 接続してプレビュー転送

LIBERA FX の **View → Preview connections** で
**Copy manual connection information** と **Copy public TLS certificate for Godot** を取得します。
実行中の Godot シーンに同梱スクリプトを追加します。

```gdscript
var desktop_preview: Node

func _ready() -> void:
    desktop_preview = preload("res://addons/liberafx/liberafx_live_preview.gd").new()
    add_child(desktop_preview)
    # 接続する前に、このエフェクトが使う資源を登録します。

func connect_desktop(invitation_json: String, public_pem: String) -> Error:
    return desktop_preview.connect_package_local(
        $LibraEffectPlayer, invitation_json, public_pem)
```

引数は一時的なローカル接続 UI などからメモリー上で渡します。
招待をコードやシーンへ固定保存しないでください。
戻り値と `preview_error`／`frame_rejected` を確認します。
接続スクリプトと `liberafx_package_preview.gd` は隣に置きます。同梱フォルダーはこの配置です。

接続後は LIBERA FX で編集・再生して Godot の画面を確認します。
接続中の専用 Player を別途 Play／Stop／advance しないでください。
後方シークは再生成・再実行、上限120秒です。追い付き処理には上限があり、
GPU を任意フレームへ完全復元する保証ではありません。
[Godot ライブプレビュー詳細](https://github.com/Libra-Works/LibraFX/blob/main/Runtime/Integrations/Godot/LIVE_PREVIEW.txt)も参照してください。

### 5. 通常再生用データを読み込む

通常出力できる文書から `.lfxb` を出力し、Godot プロジェクトへ配置します。
ゲーム用 Player をシーンツリーへ入れ、同じ明示的資源バインドを設定した後に読み込みます。

```gdscript
var result = $LibraEffectPlayer.play_package(
    FileAccess.get_file_as_bytes("res://effects/firework.lfxb"))
if result != 0:
    push_error("LIBERA FX package load failed: %s" % result)
```

`0` はパッケージ読込成功であり、画素が出た証明ではありません。
グラフの出力通知は `game_io_event` で確認します。
ゲーム側の入力 Signal は最初の process tick 後に送ってください。
プレビュー用 Player を使い直す場合は先にライブ接続を解除します。

### 6. 更新と配布

再出力したパッケージを置き換えて再生し直します。
資源のバインドは引き続き Godot プロジェクト側の責任です。
FileAccess で読む `.lfxb` は Export Preset の
**リソース以外のファイルを含めるフィルター**へ `*.lfxb` を追加します。
対応する GDExtension と参照資源もゲームへ含め、制作元 PC のパスに依存させません。
[一括コンバーター](batch-conversion.md)でCPU／GPU・Fluid2Dを生成した後、通常の Godot export を実行します。

## ゲーム側へ渡す前の確認

対象プロジェクトと割り当て、実画面、自然終了、Stop／再生し直し、
**LIBERA FX を切断した通常再生**を確認します。
プレビュー転送成功と配布用アセット作成は別です。
ゲームからの将来の Signal を待つ launcher や root の継続 GPU は、
**End automatically when idle** を確認してください。タイムラインのループはランタイム寿命ではありません。

編集ソース・Group／Node ライブラリ・エンジンの割り当てをバージョン管理し、
Preview／debugger の招待やトークンはコミットしません。
コンソール SDK・全描画 API・配布 Player の受入保証ではないため、
[実際の確認範囲](preview-update.md)も確認してください。

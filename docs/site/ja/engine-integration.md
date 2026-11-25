---
title: SDK・ランタイムの詳細
---

[ドキュメントのトップ](index.md) · [はじめに](getting-started.md)

<span id="ゲームエンジンとの連携"></span>

# SDK・ランタイムの詳細

導入・起動・資源取得・プレビュー・アセット化・通常再生までの流れは
[Unity／Unreal／Godot 別の制作フロー](production-workflow.md)を参照してください。
自動化は[一括コンバーター](batch-conversion.md)で説明します。

SDK、ランタイム、編集用プロジェクト、配布用バイナリーの意味は[エンジンとファイルの用語](glossary.md#engine)を参照してください。

[最新 Preview](https://github.com/Libra-Works/LibraFX/releases)
から対応する SDK をダウンロードします。エンジンを閉じて ZIP を展開し、
以下に示すインストール用フォルダーだけをプロジェクトにコピーします。
各フォルダーに `VERSION.json` が入っています。

エディター・コンバーター・SDK は同じリリースを使ってください。通常再生とライブプレビューでは、コンパイル済み `.lfxp` パッケージ形式を使います。編集元の `.lfxproj` を保持し、更新時は対応するコンバーターで再出力してください。ファイル名を変えても中身の形式は変換されません。Unity のインポート検証には対応するネイティブプラグインも必要です。

| SDK | プロジェクト内の配置先 | 同梱ランタイム |
| --- | --- | --- |
| LibraForUnity | `Packages/works.libra.liberafx` | Windows x64 ネイティブブリッジ |
| LibraForUnreal | `Plugins/LibraRuntimeForUnreal` | ポータブル Core を含むソースプラグイン |
| LibraForGodot | `addons/liberafx` | Windows x64 用 Godot 4.7 GDExtension |

更新前に既存のインストールをバックアップしてください。Unity では変更していない
アセットの `.meta` を残し、パッケージやネイティブプラグインの重複登録を解消します。
Godot では生成済みの `.gdextension.uid` を残します。Unreal では上書きで混ぜず、
インストール用フォルダーごと入れ替えます。開発用ジャンクションのリンク先は削除しないでください。

### 検証範囲

SDK は以下の描画・Fluid2D・Spawnable のネスト・内部 CPU に対応します。
ローカル受入確認では3エンジンとも実ピクセルと解放を確認しています。
Unreal は UE 5.7.4 の Clang コンパイル（`-NoLink`）、MSVC の Editor リンク、
D3D11／SM5 の実ピクセルを確認しています。
Clang コンパイルだけで、MSVC 製エンジンとのリンクやコンソール SDK の確認を意味しません。
詳しい環境は[リリースノート](release-notes.md)を参照してください。
コンパイル・インポート成功だけを画面表示の証拠にしないでください。

<details class="page-toc" markdown="1">
<summary>このページの内容</summary>

* TOC
{:toc}

</details>

## 座標系と単位

エフェクトは **メートル単位、X が右、Y が上、Z が前** として制作します。ランタイムの座標基底がホストとの境界で空間データを変換するため、エンジンごとにエフェクトのグラフを回転させる必要はありません。

| ホストの既定値 | 制作用 X / Y / Z の対応先 | 1メートルの単位数 |
| --- | --- | --- |
| Unity | +X / +Y / +Z | 1 |
| Unreal | +Y / +Z / +X | 100 |
| Godot | +X / +Y / −Z | 1 |

SDK は、位置・ベクトル・方向・法線・角速度ベクトル・変換行列という役割に応じて変換します。色や単なる数値のベクトルは、自動的に空間データとは扱いません。独自ホストでは、軸の対応と符号、単位をランタイムとの境界に設定し、外部から渡す空間入力にも同じ規約を使ってください。変換を二重に掛けないようにします。

この制作手順にはエディター側の Y-up／Z-up 切り替えはありません。グラフは共通の Y-up で作り、Z-up の独自ゲームなどでは SDK 側の座標基底を設定します。

<span id="rendering"></span>


## 描画ごとのシーン Component を作らない方式

対応する同じリリースのアダプターを導入してください。エフェクト全体を再生する
Component／Node は残します。通常の Model／Sprite Draw、CPU Sprite 粒子、
FlexiblePlane は、描く物ごとの MeshComponent・MeshRenderer・MeshInstance3D を
作らず、エンジンの描画データとして扱います。アセット・マテリアル・描画基盤は
エンジンを使い、共通 Core に独自の描画エンジンを持ち込みません。

| エンジン | 描画の渡し先 |
| --- | --- |
| Unity | `Graphics` の直接描画と、互換な不透明描画のインスタンシング。CPU 粒子と帯の Mesh は再利用します。 |
| Unreal | native primitive の記述と SceneProxy。不透明描画をまとめ、透明物はソート用の primitive を分けます。 |
| Godot | RenderingServer の instance・Mesh／MultiMesh RID。粒子データを一括転送し、帯の surface を再利用します。 |

メッシュ、マテリアル、パラメーター、変換行列や描画順が異なると、まとめられない場合があります。
3エンジンとも、鏡像や特異なワールド変換を通常描画のインスタンシング対象から外し、
個別描画時の面のカリングを保ちます。
Component を消しても、描画命令が1回になったり、リソースが不要になったり、
必ず高速になったりするわけではありません。必要な軽量 primitive／RID は別々に残せます。
既存 GPU シミュレーションの計算方式は変更していません。

### Unity の Sprite 描画順に関する制限

複数画像を重ねる Sprite も直接描画し、**MeshRenderer へ戻す代替経路は使いません**。
バッチ化前の独立した `LiberaFxRenderOrdering` に、描画キュー、作者が指定した `drawOrder`、
カメラからの深さ、登録中に変わらない ID を保持します。透明物や明示的な順序付きの
描画はインスタンシングしません。

これは Unity への**投入順で、画面全体の透明描画順を保証しません**。
確認した Built-in／URP の Graphics 経路では奥行きによる再ソートが入り、
従来の `Renderer.sortingOrder` や、外部 Renderer を挟む順序は再現できません。
Renderer 廃止を優先する意図的な変更です。将来、保持した情報を使ってソート・描画経路を
差し替えられる構造にしていますが、その追加バックエンドは未実装です。

### ローカルの検証範囲と負荷の数値

Unity 7000.0.0a16／D3D11 の Built-in と URP 17.7 では、通常描画のバッチ化、
CPU Sprite、固定点の帯、Sprite 合成と Stop 後の解放を実ピクセルで確認しています。
Godot 4.7.2 では root／子の不透明バッチ、CPU Sprite、固定点の帯、更新を保持した状態での
所有者の移動・表示切替、world 切替、Stop を実描画で確認しています。
すべての透明シーン、履歴形式、エンジン版、描画バックエンド、配布用 Player の
検証を意味しません。

Unreal ソースは UE 5.7.4 の Clang compile-only と MSVC リンクに加え、D3D11／SM5 で
不透明インスタンシング、CPU Sprite の RGB／半透明、固定 up の帯の頂点色、
Stop 後に描画が消えることを実ピクセルで確認しました。片面 Plane でも鏡像変換を含む
3回の生成・削除を確認しています。NullRHI 単独の成功は画面表示の証拠ではありません。
他の RHI、配布用ゲーム、コンソール SDK は未検証です。詳細は各版の `USAGE.txt` を
参照してください。

統計は論理的な Draw／粒子数、バッチ数、native instance、送信・staging したデータを
区別します。項目名は各アダプターの `USAGE.txt` を参照してください。
容量4件の MultiMesh バッファーに生存粒子が3件ある場合も、転送するバッファーは4件です。
staging の byte 数は、GPU 転送完了量やフレーム時間の実測値ではありません。

[Fluid2D](fluid-2d.md)も実 GPU で確認しています。
[ネストした CPU の所有関係](cpu-particles.md#spawnable-の所有関係)は、
親・子の期限後も生きる粒子、最終解放、再生し直しを3エンジンで確認しました。
実装の受入確認であり、全配布バイナリー・全環境・全描画 API・配布 Player の認証ではありません。

## 最初のエフェクトを再生する

最初は描画・GPU ノードを含まない CPU のエフェクトパッケージ（`.lfxp`）を使うと、
メッシュやマテリアルを割り当てる前に、読み込みと名前付き Game I/O を切り分けられます。
`.lfxproj` を保存し、`Converter` フォルダーに同梱された `LiberaFxConverter` で変換します。
Windows の例は次のとおりです。

```powershell
.\LiberaFxConverter.exe convert C:\Effects\input.lfxproj C:\Effects\effect.lfxp
```

エディターの **VFX を書き出す...** も同じ `.lfxp` パッケージを作ります。動作確認には、
名前付き Output Signal など、再生できたことが分かる出力をグラフに用意します。

### Unity

`.lfxp` を `Assets/` 以下に置き、インポートされたアセットを `LiberaFxSpawnableComponent`
に割り当てます。**Play On Enable** を有効にして Play Mode に入るか、`Play()` を呼びます。
開始に失敗した場合は `LastError` を確認し、出力は `GameIoEventReceived` で受け取ります。

### Unreal Engine

プラグインをコピーした後、プロジェクトの Editor ターゲットをビルドします。Content Browser
でコンパイル済み `.lfxp` パッケージを `ULibraSpawnableAsset` としてインポートします。
Actor に `ULibraEffectComponent` を追加して **Package** に割り当てるか、
`ALibraEffectActor` を配置し、そのコンポーネントの **Package** に割り当てます。
既定ではコンポーネントが自動再生します。失敗時は `LastError` を確認し、出力は
`OnGameIoEvent` で受け取ります。内部用 `ULibraSpawnableComponent` は
`ULibraEffectComponent` が生成・管理するため、作者が別途追加する必要はありません。
GPU 専用アセットは `ULibraGpuEffectAsset` として引き続き利用でき、コンポーネントの
**GpuEffect** スロットに割り当てます。エフェクト全体を使う場合は `.lfxp` を使います。

### Godot

`.lfxp` を `res://` 以下に置き、`LibraEffectPlayer` を追加します。プレイヤーがシーンツリーに
入った後、`play_package(FileAccess.get_file_as_bytes("res://effect.lfxp"))` を呼びます。
戻り値 `0` は読み込み成功です。最初の処理フレームを待ってから入力シグナルを送り、
`game_io_event` で出力を確認します。ゲームと一緒に配布する場合は、エクスポートプリセットの
非リソースフィルターに `.lfxp` を含めます。

<a id="prepared-playback"></a>

## 再生領域の事前確保と共有実行

SDK はロード中にエフェクトの再生領域を確保し、再利用できます。
独立した複数のエフェクトは、上限のある共通の実行管理へまとめます。
エフェクトごとに専用のワーカースレッドを増やす方式ではありません。
描画やエンジンのコールバックは、それぞれのエンジンスレッドの境界で処理します。

| エンジン | ロード中の準備 | 準備前に設定する項目 |
| --- | --- | --- |
| Unity | シーンに設定済みのエフェクトは `Awake` で準備。動的に割り当てる場合は `PrewarmEffect()` を呼びます。 | Spawnable コンポーネントの `usePreparedHosts`、`preparedHostCapacity`、`preparationWorkers`、`useSharedSimulation`。 |
| Unreal | 割り当て済み `.lfxp` パッケージは `ULibraEffectComponent` が `BeginPlay` で準備します。 | `Libra Runtime\|Performance` では **Use Shared Simulation** を設定できます。容量とワーカー数はランタイム既定値を使います。 |
| Godot | シーンツリー内のプレイヤーで `prewarm_package(bytes, capacity, workers)` を呼びます。 | `play_package` より前に同時再生数を予約します。通常の処理は共有実行し、明示的な `advance(delta)` は即時実行します。 |

ここでの容量は、粒子数ではなく**エフェクトの同時再生数**です。
既定値は**同じパッケージ・設定のプールごとに256個**、準備ワーカー数は4です。
既存のコンポーネントに保存済みの値や、引数で明示した容量は維持するため、
必要に応じてそちらも変更してください。
同じエフェクトを8個同時に再生するなら、8個の再生領域を予約します。
満杯の場合は `InsufficientWork` を返し、9個目を暗黙に追加確保しません。
使用中の再生を終了してから、その容量を再利用してください。
再利用時は時刻、入力、行列、粒子の識別情報、イベントがリセットされるため、
新しい再生に必要な値を改めて設定します。

Spawnable のネストと[プロシージャルメッシュの放出](cpu-particles.md)にも対応します。
親子の Signal と終了処理の順序は、同じホスト内で保ちます。
メモリは各テンプレートの子の最大数にも応じて増えます。
変形メッシュの更新や階層のルーティングでは、再生中にメモリを確保する場合があります。
事前確保は、すべてのフレームで確保がゼロになるという意味ではありません。
エンジンのメッシュ、テクスチャー、Unreal の CPU Sprite 用材質などの準備も必要です。

### 独自のネイティブノード

`CustomNodePolicy` で、`reusable`（再利用）、`workerSafe`（ワーカー実行）、
`parallelPreparation`（並列初期化）を別々に宣言できます。
Unity・Godot は `host::ProjectNodeRegistry`、Unreal は
`FLibraNativeNodes::Register(descriptors, policies)` で渡します。
独自モジュールも対応する SDK で再ビルドしてください。既存の descriptor ABI-1 の構造は維持しています。

指定がない独自コールバックは事前確保プールへ入れず、共有実行ではワーカーの完了後に
呼び出し元スレッドで動きます。「再利用できる」という指定だけでは、ワーカー実行や
初期化の並列化は許可されません。初期化・後始末・共有する外部の状態まで含めて、
その条件を満たす場合だけ、それぞれを有効にします。

### 確認した範囲

Windows 上で、新規生成と再利用・共有実行の粒子状態、子の寿命と Signal の順序、
独自コールバックの状態・後始末・実行スレッド、メモリ検査を確認しました。
実エンジンでは Unity 7000.0.0a16／D3D11 の検証用 unlit SRP、Godot 4.7.2／OpenGL、
Unreal 5.7 で、プロシージャルメッシュとネストした CPU エフェクトの事前確保・共有実行を確認しています。
Unity・Godot では粒子の表示、Stop 後の消去、再再生も確認しました。
Unreal のこの経路はタスクとシーンへの反映の検証で、配布用 Player や描画画像の一致までを示すものではありません。

エディター・コンバーター・ネイティブ連携の版を揃えてください。
ドキュメントが更新されても、以前の Preview バイナリーの機能は変わりません。
更新時は[リリースノート](release-notes.md)も参照してください。

## エフェクトを拡張する

- **C# または GDScript:** ルートグラフに `action.*` という名前の Output Signal を用意し、
  Unity の `BindScriptAction` または Godot の `bind_script_action` で関数を登録します。
  関数はエンジンのメインスレッドで呼ばれ、戻り値は名前付き Input Property に渡して
  次の評価から使えます。描画には既存の Model Draw や Sprite ノードを使えます。
- **プロジェクト独自の C++ ノード:** ノード JSON の UUID、ABI、ポートに対応する
  ディスクリプターを登録します。Unreal ではモジュールの起動時に登録し、
  Unity・Godot ではネイティブブリッジに組み込みます。`.lfxp` パッケージのルートと子に対応します。
  同じ評価中に計算結果が必要な場合はこちらを使います。

登録手順、コード例、検証範囲は
[SDK 配布リファレンス（英語）](https://github.com/Libra-Works/LibraFX/blob/main/docs/sdk-distribution.md#project-defined-nodes-in-spawnable-packages)
を参照してください。同梱の Windows 用バイナリは、ほかのプラットフォーム、描画バックエンド、
コンソール SDK の動作を保証するものではありません。

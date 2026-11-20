---
title: ゲームエンジンとの連携
---

[ドキュメントのトップ](index.md) · [はじめに](getting-started.md)

# ゲームエンジンとの連携

導入・起動・資源取得・プレビュー・アセット化・通常再生までの流れは
[Unity／Unreal／Godot 別の制作フロー](production-workflow.md)を参照してください。
自動化は[一括コンバーター](batch-conversion.md)で説明します。

SDK、ランタイム、編集用プロジェクト、配布用バイナリーの意味は[エンジンとファイルの用語](glossary.md#engine)を参照してください。

[最新 Preview](https://github.com/Libra-Works/LibraFX/releases)
から対応する SDK をダウンロードします。エンジンを閉じて ZIP を展開し、
以下に示すインストール用フォルダーだけをプロジェクトにコピーします。
各フォルダーに `VERSION.json` が入っています。

以下の統合 `.lfxb` パッケージの手順は、同じソース版から更新したエディター・コンバーター・
SDK が必要です。2026.11.8 以前の SDK と混在させないでください。
更新後の SDK も旧 `.lfxp` パッケージを引き続き読み込めます。

旧バイトコード単体のアセットを、新しいパッケージ型へ自動移行する処理はありません。
旧 `.lfxb` を新規出力に置き換える場合は、別のアセットとしてインポートして
コンポーネントの参照を明示的に付け替えてください。ファイル名の変更や再インポートだけでは
アセット型は移行しません。Unity のインポート検証には対応するネイティブプラグインも必要です。

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

2026.11.22 より後の Preview 更新は、以下の描画・2D 煙／色密度・Spawnable ネスト／内部 CPU に対応します。
ローカル受入確認では3エンジンとも実ピクセルと解放を確認しています。
Unreal は UE 5.7.4 の Clang コンパイル（`-NoLink`）、MSVC の Editor リンク、
D3D11／SM5 の実ピクセルを確認しています。
Clang コンパイルだけで、MSVC 製エンジンとのリンクやコンソール SDK の確認を意味しません。
詳しい環境は[更新ガイド](preview-update.md)を参照してください。
コンパイル・インポート成功だけを画面表示の証拠にしないでください。

<span id="rendering"></span>

## 描画ごとのシーン Component を作らない方式

**2026.11.22 より後の Preview 更新に描画移行を含みます**。旧 ZIP 自体が更新されるわけでは
ありません。対応する更新版アダプターを導入してください。エフェクト全体を再生する
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

今回の更新では[2D の煙・色密度](fluid-2d.md)も実 GPU で確認しています。
[ネストした CPU の所有関係](cpu-particles.md#spawnable-の所有関係)は、
親・子の期限後も生きる粒子、最終解放、再生し直しを3エンジンで確認しました。
実装の受入確認であり、全配布バイナリー・全環境・全描画 API・配布 Player の認証ではありません。

## 最初のエフェクトを再生する

最初は描画・GPU ノードを含まない CPU のエフェクトパッケージ（`.lfxb`）を使うと、
メッシュやマテリアルを割り当てる前に、読み込みと名前付き Game I/O を切り分けられます。
`.lfxproj` を保存し、`Converter` フォルダーに同梱された `LiberaFxConverter` で変換します。
Windows の例は次のとおりです。

```powershell
.\LiberaFxConverter.exe convert C:\Effects\input.lfxproj C:\Effects\effect.lfxb
```

エディターの **VFX を書き出す...** も同じ `.lfxb` パッケージを作ります。動作確認には、
名前付き Output Signal など、再生できたことが分かる出力をグラフに用意します。

### Unity

`.lfxb` を `Assets/` 以下に置き、インポートされたアセットを `LiberaFxSpawnableComponent`
に割り当てます。**Play On Enable** を有効にして Play Mode に入るか、`Play()` を呼びます。
開始に失敗した場合は `LastError` を確認し、出力は `GameIoEventReceived` で受け取ります。

### Unreal Engine

プラグインのコピー後にプロジェクトの Editor ターゲットをビルドします。
Content Browser で `.lfxb` をインポートし、アセットをレベルにドラッグすると
**Libra Effect** コンポーネント付きのアクターが配置されます。
**Auto Play** を有効にして Play In Editor を開始するか、`Play()` を呼びます。
出力は `OnGameIoEvent` で受け取ります。

### Godot

`.lfxb` を `res://` 以下に置き、`LibraEffectPlayer` を追加します。プレイヤーがシーンツリーに
入った後、`play_package(FileAccess.get_file_as_bytes("res://effect.lfxb"))` を呼びます。
戻り値 `0` は読み込み成功です。最初の処理フレームを待ってから入力シグナルを送り、
`game_io_event` で出力を確認します。ゲームと一緒に配布する場合は、エクスポートプリセットの
非リソースフィルターに `.lfxb` を含めます。

## エフェクトを拡張する

- **C# または GDScript:** ルートグラフに `action.*` という名前の Output Signal を用意し、
  Unity の `BindScriptAction` または Godot の `bind_script_action` で関数を登録します。
  関数はエンジンのメインスレッドで呼ばれ、戻り値は名前付き Input Property に渡して
  次の評価から使えます。描画には既存の Model Draw や Sprite ノードを使えます。
- **プロジェクト独自の C++ ノード:** ノード JSON の UUID、ABI、ポートに対応する
  ディスクリプターを登録します。Unreal ではモジュールの起動時に登録し、
  Unity・Godot ではネイティブブリッジに組み込みます。`.lfxb` パッケージのルートと子に対応します。
  同じ評価中に計算結果が必要な場合はこちらを使います。

登録手順、コード例、検証範囲は
[SDK 配布リファレンス（英語）](https://github.com/Libra-Works/LibraFX/blob/main/docs/sdk-distribution.md#project-defined-nodes-in-spawnable-packages)
を参照してください。同梱の Windows 用バイナリは、ほかのプラットフォーム、描画バックエンド、
コンソール SDK の動作を保証するものではありません。

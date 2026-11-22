---
title: フレキシブルプレーン描画
---

[ホーム](index.md) · [エディターリファレンス](editor-reference.md)

# フレキシブルプレーン描画

**Flexible Plane Draw** は、順番に並んだ点をテクスチャ付きの帯にします。固定した形には点リスト、動いた跡を残す軌跡には位置の履歴を使います。

軌跡は異なる位置が2点以上たまってから表示されます。テクスチャも指定してください。描画の実装は [SDK・ランタイムの詳細](engine-integration.md#rendering)で説明します。

<details class="page-toc" markdown="1">
<summary>このページの内容</summary>

* TOC
{:toc}

</details>

## 作り方

「描画」の **Flexible Plane Draw** と、「フレキシブルプレーン」の
**Flexible Plane Point List** または **Flexible Plane Point History** を
追加し、ソース出力を Draw の「点」へ接続します。テクスチャを明示的に
指定してください。代用アセットはありません。軌跡では動く FloatX3 を
History の「位置」に接続します。インスタンス開始後にサンプリングし、
異なる位置が２点以上できてから帯が見えます。自由形状は List の
「点 JSON」を編集します。位置 0～3 の FloatX3 入力で対応する点を動的に
上書きできます。それ以降の点は JSON で指定します。

テクスチャのプロパティでは既存のリソースブラウザーから画像を選べます。
割り当ては元に戻す／やり直しに対応し、主テクスチャだけを変更します。
帯用マテリアルはグラフのマテリアルスロットではなく、ホスト側で設定します。

単独の root 帯や履歴は、それ自体ではアイドル状態のエフェクトを維持しません。
`AutoDestroyWhenIdle` は Spawnable テンプレートがアイドルになると root を
終了することがあります。テンプレートのない root 専用パッケージはこの条件では
自動終了しません。明示的な停止または有限の再生時間まで帯を維持するには、
root の `AutoDestroyWhenIdle` を無効にし、寿命を明示的に管理してください。

点 JSON は２～64 個のオブジェクトからなる配列で、文字列全体は最大
4096 文字です。各点には `position`（３数値）、`width`（０以上）、
`color`（４数値）のみが必須です。数値は有限の 32-bit float 範囲です。

```json
[{"position":[0,0,0],"width":1,"color":[1,1,1,1]},
 {"position":[1,0,0],"width":0.5,"color":[1,0.5,0,1]}]
```

色は線形・非乗算済み RGBA、幅は点と同じ座標単位の**全幅**です。
各点の幅と色がプロファイルを構成し、三角形内はレンダラーが補間します。
位置 0～3 を接続する場合、対応する点がリスト内に必要です。

| ノード | 入力・設定 | 初期値と制限 |
|---|---|---|
| Draw | 点 | Point List または Point History への接続が必須 |
| Draw | テクスチャ | 空。明示的なリソースが必須 |
| Draw | 幅の倍率 | 1。有限かつ０以上。数値ピン対応 |
| Draw | 乗算色 | `[1,1,1,1]`。有限 RGBA。数値ピン対応 |
| Draw | カメラ向き | true。false なら固定上方向 |
| Draw | 固定上方向 | `[0,1,0]`。点と同じ座標系の有限ベクトル |
| Draw | UV を繰り返す | false: 全長で伸張。true: 繰り返し |
| Draw | 繰り返し長 | 1。正の有限数、U 一周期あたりの座標単位 |
| Point List | 点 JSON | `[0,0,0]` と `[1,0,0]` の白・幅１の２点 |
| Point List | 位置 0～3 | 未接続時は対応する JSON の位置。FloatX3 |
| Point History | 位置 | `[0,0,0]`。有限 FloatX3 |
| Point History | 幅 | 1。有限かつ０以上の全幅。Float |
| Point History | 色 | `[1,1,1,1]`。有限 RGBA。FloatX4 |
| Point History | 最大点数 | 64。整数 2～4096 |
| Point History | 最大保持時間 | １秒。正の有限秒数 |

固定上方向では接線と up の外積、カメラ向きでは接線と「点からカメラ
位置への方向」の外積を使用します。カメラは前方向ではなく、点と**同じ
座標系の位置**を渡します。平行やゼロの場合は決定的な垂直方向に
フォールバックします。隣接点の距離が `1e-6` 以下なら重複としてまとめ、
異なる点が２点未満なら空の描画です。幅０は有効です。U は伸張時に
経路長に対して 0～1、繰り返し時に経路長 / 繰り返し長、V は 0～1。
テクスチャの wrap、ブレンド・カリング、変換、エンジン間の座標変換は
ホストが担当します。

## ホストへの統合と所有権

`FlexiblePlaneDrawSchema.CreateDraw` は `render_config.nodes` に
kind `flexiblePlane`、`nodeId`、`assetPath`、既存形式の `inputs`、
version 1 の `flexiblePlane` を返します。source は `list` または
`history`。list の `points` は `position`、`width`、`color`、history は
`capacity` と `maxAge` を持ちます。`facing`、`up`、`uvMode`、
`repeatLength` は上記の設定に対応します。バインディング ID は Draw の
`width`・`tint`、list の `position0`～`position3`、history の
`position`・`pointWidth`・`pointColor` です。source ID と component は
通常の VM 値を指します。メタデータノード自体は VM 命令にせず、その
数値入力元は引き続き評価・バインド可能にする必要があります。
`assetPath` には解決済みの主テクスチャを格納します。`materials` と
`textures` は空配列で、マテリアルの指定ではありません。統合側の
レンダラーはテクスチャ対応の既定帯マテリアルを用意するか、未用意を
明示する必要があります。このメタデータ自体はマテリアルを提供しません。

C ABI は `Runtime/include/liberafx/LbFlexiblePlaneDraw.h`、実装は
`Runtime/Core/LbFlexiblePlaneDraw.cpp` です。ネイティブプラグインでは
`LIBRA_INTEROP_BUILD`、静的ホストではコンパイル元と利用元の両方に
`LIBRA_FLEXIBLE_STATIC` を定義します。コアが別の共有モジュールの場合は
代わりに両側で `LIBRA_FLEXIBLE_API` をそのモジュールの export/import
マクロに定義できます。生成 API は呼び出し側所有の
点・頂点・32-bit index バッファーを使用します。空バッファーで必要数を
取得し（非空なら `LIBRA_INSUFFICIENT_WORK`）、確保後に再実行できます。
失敗時に頂点・index バッファーは変更されません。不正入力や算術
オーバーフローを含め結果を確認してください。入力は最大 4096 点で、
入出力バッファーの重複は禁止です。異なる点ごとに２頂点、区間ごとに
６ index を割り当てなしで生成します。

履歴状態と時刻付きストレージは **各エフェクトインスタンスと点ソース**
が所有します。Spawnable 子インスタンス同士も共有しません。生成時・再利用時
に初期化／リセットします。スコープ内の全 Draw の更新前に共有履歴の
`BeginUpdate()`（Unity）または `beginUpdate()`（C++）を１回呼びます。
複数 Draw が同じソースを参照しても１更新につき１回サンプルし、同一更新内で
値が食い違えばエラーにします。時刻は有限のインスタンス秒数です。容量超過時は最古を
破棄し、`maxAge` より古い点を削除します。同時刻は最新を上書きし、時刻が
戻れば履歴を消して新しい点から開始します。不連続な前方シークも明示的に
リセットしてください。過去の補間・再生はありません。履歴を別の呼び出し側
バッファーへコピーし、Draw の幅・色を**コピーに**乗算してから生成します。
保存済み履歴には繰り返し乗算しません。list も同様に乗算します。

ポータブル API だけではメッシュ送信、アセット管理、インスタンス変換、
ピクセル描画は行いません。移行後のソースでは、Draw・インスタンスごとに
再利用する形状を所有し、対応するシーン Component は作りません。

* Unity は明示的なテクスチャバインディングに、マテリアルをちょうど１つ
  `defaultMaterials[0]` として指定します。シェーダーは `_MainTex` を
  サンプルし、頂点色を乗算する必要があります。カメラ向きでは `Camera.main`
  を使用します。メッシュとマテリアルの複製を所有しますが、
  描画用の GameObject／MeshRenderer は作りません。
* Unreal は明示的にバインドしたテクスチャと、`builtin:flexible-plane` に
  登録したマテリアルが必要です。マテリアルはグローバルな `Texture`
  パラメーターと頂点色を使用します。カメラ向きでは指定カメラ、なければ
  プレイヤーカメラを使用します。native primitive／proxy の頂点色は８ bit
  のため 0～1 の範囲外はエラーです。FlexiblePlane 用 Component は作りません。
* Godot のパッケージ描画は明示的に登録したテクスチャと、頂点色付き・非ライティング・
  透明の `StandardMaterial3D` を使用します。カメラ向きではプレイヤー指定または
  現在のビューポートのカメラが必要です。root と各 Spawnable 子は独立した履歴を持ち、時間の逆行で
  履歴を消去します。明示的な前方シークを実装するホストは更新前に
  `resetFlexibleHistories()` を呼んでください。MeshInstance3D の代わりに
  RenderingServer の RID と再利用する動的 ArrayMesh surface を使用します。
  surface 更新、表示切替、所有者の変換と Stop は実描画で確認しています。
  他バックエンドや配布用 Player は別途検証が必要です。

エンジンオブジェクトはオーナースレッド専用です。カメラ向きではカメラ位置を
各メッシュのローカル座標に変換し、変換が特異ならエラーです。固定上方向では
カメラは不要です。更新に失敗した
場合は古いメッシュの表示を続けず、ホストが停止またはエラーを通知してください。
対応版のプラグイン／SDK が必要です。旧 SDK は未対応を明示し、
描画を黙って省略してはいけません。`.lfxb` のメタデータは既存 LFXP
コンテナーに残り、各コンシューマーの対応を意味するものではありません。

## 次に進む

- [エンジンに接続してエフェクトを再生する](production-workflow.md)。
- [表示されない・思った動きにならないときの確認](troubleshooting.md)。
- [標準ノードの働きを調べる](node-reference.md)。

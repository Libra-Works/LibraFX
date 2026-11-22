---
title: LIBERA FX ドキュメント
---

# LIBERA FX ドキュメント

LIBERA FX は、Unity・Unreal Engine・Godot で再生するエフェクトを、
ノードグラフで作るエディターです。エディターでグラフを組み、
同梱のコンバーターで変換してから、ゲームエンジン上で再生します。

**最新 Preview のダウンロード:** [GitHub Releases](https://github.com/Libra-Works/LibraFX/releases)

| ガイド | 内容 |
| --- | --- |
| [はじめての画面操作](workspace-tour.md) | 各ウィンドウの場所から、ノードの選択、値の編集、ピンの接続、プレビューまで順に操作します。 |
| [最初の噴水を作る](first-fountain.md) | 同梱サンプルを触るコースと、空のグラフから作るコースで CPU パーティクルを体験します。 |
| [リリースノート](release-notes.md) | バージョンごとの変更内容・更新方法・確認済みの環境を読めます。 |
| [制作からゲーム組み込みまで](production-workflow.md) | Unity・Unreal・Godot 別に、導入・起動・資源取得・プレビュー・アセット化・配布まで進めます。 |
| [一括コンバーター](batch-conversion.md) | CPU 系の一括出力、共有ライブラリの解決、ゲームビルドへの組み込みを説明します。 |
| [はじめに](getting-started.md) | エディターを導入し、同梱の **Afterimage Gate** を開いて、エフェクトを動かすシグナルをたどります。 |
| [用語集](glossary.md) | ノード、Signal、Spawnable、ランタイムなどの言葉を、短い例と一緒に確認できます。 |
| [ウィンドウとパネルのリファレンス](editor-reference.md) | ツール、設定、ダイアログの使い方を実画面と操作例で確認できます。 |
| [GPU パーティクル制作](gpu-particles.md) | GPU フロー、ビジュアルカーネル、型付き DSL、ビルボード設定を編集します。 |
| [2D の煙・水・炎](fluid-2d.md) | リソース先頭の格子ソルバー、セル外力、専用描画、生成出力を扱います。 |
| [プロシージャルリソース](procedural-resources.md) | 順序付き Flow からモデル形状、GPU テクスチャー、マテリアルを作ります。 |
| [CPU パーティクル制作](cpu-particles.md) | Spawn／Update 子グラフ、粒子の放出、CPU の Property／Signal のやり取りを説明します。 |
| [Flexible Plane Draw](flexible-plane-draw.md) | 自由配置の点列や、位置の履歴から帯状のポリゴンを作ります。 |
| [グラフ制作ガイド](authoring.md) | ピンの接続、カーブ、Spawnable の寿命、Group の再利用を説明します。 |
| [標準ノード図鑑](node-reference.md) | カテゴリーとサブカテゴリーから標準ノードを探し、入出力ピンや既定値を調べられます。 |
| [スカラー演算の使い方](scalar-math-nodes.md) | 新しい10ノードで、値の折り返し、なめらかな変化、しきい値、逆補間を扱います。更新後のランタイム・SDK が必要です。 |
| [クォータニオンと乱数](quaternion-random.md) | 回転を合成し、シードとサンプル番号を指定して再現可能な乱数を使います。 |
| [ゲームエンジンとの連携](engine-integration.md) | SDK を導入してエフェクトを再生し、スクリプトや独自 C++ ノードで拡張します。 |
| [SDK 配布リファレンス（英語）](https://github.com/Libra-Works/LibraFX/blob/main/docs/sdk-distribution.md) | ソースからのビルド、パッケージの組み立て、対応環境と検証範囲を確認できます。 |

[![LIBERA FX エディターに表示された Afterimage Gate のグラフ、タイムライン、Sequence のインスペクター](../../images/afterimage-gate-editor.png)](../../images/afterimage-gate-editor.png)

*エディター上の Afterimage Gate グラフです。ゲームエンジンによる描画結果ではありません。*

## フルイドは 2D を先に充実

開発ソースでは **2D の煙、横から見る自由表面の水、上から見る浅水、燃料・温度による燃焼**に対応します。
リソース先頭の Flow、セル外力、専用描画、同一スコープ内の生成テクスチャ・フィールド参照は、
実画面付きの[フルイドの説明](fluid-2d.md)を参照してください。
対応するエディターと SDK ソースを組み合わせてください。公開済みアーカイブの再配布は行っていません。

Unity・Unreal・Godot の描画改修、CPU パーティクルの Flow、GPU 放出形状も含みます。
Spawnable のネストと内部 CPU 粒子は、個体ごとの所有・寿命に対応します。
**2D を充実させてから 3D へ進める** [issue #69](https://github.com/Libra-Works/LibraFX/issues/69) の対応は完了しました。3D フルイドは今後の対象です。
プロシージャルの説明では、生成モデル・テクスチャー・マテリアルと上限を紹介します。
共通リソース計画の契約と宣言は
[issue #68](https://github.com/Libra-Works/LibraFX/issues/68)の基礎です。
任意の実行リソースグラフやメイン画面への書き込みには、追加のアダプター対応が必要です。

[ソースリポジトリ](https://github.com/Libra-Works/LibraFX) ·
[MIT ライセンス](https://github.com/Libra-Works/LibraFX/blob/main/LICENSE)

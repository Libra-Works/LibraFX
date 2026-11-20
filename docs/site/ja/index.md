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
| [はじめに](getting-started.md) | エディターを導入し、同梱の **Afterimage Gate** を開いて、エフェクトを動かすシグナルをたどります。 |
| [用語集](glossary.md) | ノード、Signal、Spawnable、ランタイムなどの言葉を、短い例と一緒に確認できます。 |
| [ウィンドウとパネルのリファレンス](editor-reference.md) | ツール、設定、ダイアログの使い方を実画面と操作例で確認できます。 |
| [GPU パーティクル制作](gpu-particles.md) | GPU フロー、ビジュアルカーネル、型付き DSL、ビルボード設定を編集します。 |
| [2D の煙・インク](fluid-2d.md) | リソース先頭の GPU 格子ソルバー、発生源、密度テクスチャを扱います。公開 Preview には未収録のソース機能です。 |
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

## 今後の方針：フルイド（流体）

最初は **smoke-first：GPU による 2D の煙・色密度の移流**です。
更新後のソースには固定の格子ソルバーとリソース先頭の Flow を追加しています。
設定・エンジン検証・制限は[2D の煙・インク](fluid-2d.md)を参照してください。
**公開済み 2026.11.22 Preview には含まれません**。
エディター・コンバーター・SDK は対応するソース版を組み合わせます。

Unity・Unreal・Godot の描画改修、CPU パーティクルの Flow、GPU 放出形状は
ソースへ反映済みです。**2D を検証してから 3D へ進めます**。
液体や燃焼は今後の対象で、煙・色密度の計算は水面や炎の実装ではありません。
今回のリソース／ターゲット宣言は
[issue #68](https://github.com/Libra-Works/LibraFX/issues/68)の一部を具体化したもので、
任意のリソースグラフやメイン画面へのアクセスではありません。

[ソースリポジトリ](https://github.com/Libra-Works/LibraFX) ·
[MIT ライセンス](https://github.com/Libra-Works/LibraFX/blob/main/LICENSE)

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

煙・炎などの気体と、水・粘性のある液体の両方を将来の対応対象とします。
計算方式、CPU／GPU の役割、描画方式は今後設計します。
流体シミュレーションは **未実装で、今回の Preview には含みません**。
パーティクルや帯状ポリゴンが使えることは、流体ソルバーへの対応を意味しません。

[ソースリポジトリ](https://github.com/Libra-Works/LibraFX) ·
[MIT ライセンス](https://github.com/Libra-Works/LibraFX/blob/main/LICENSE)

---
title: LIBERA FX ドキュメント
---

# エフェクトを作り、ゲームで動かす

LIBERA FXは、**Unity・Unreal Engine・Godotで再生するエフェクト**を作るノードエディターです。
ノードをつないで動きと見た目を作り、エンジンのプレビューで確認して、ゲーム用に書き出します。
初めての方は、画面を操作する入門ガイドから進めてください。SDKやプログラムの詳細は別の項目にまとめています。

## 最初はこの3ステップ

| 順番 | ガイド | できるようになること |
| --- | --- | --- |
| 1 | [導入と最初の起動](getting-started.md) | エディターを起動し、プレビューの方法を選びます。 |
| 2 | [画面の基本操作](workspace-tour.md) | ノードの選択・値の編集・配線・階層の移動を覚えます。 |
| 3 | [最初の噴水を作る](first-fountain.md) | CPUパーティクルの放出量・速さ・重力・大きさ・寿命を変えます。 |

グラフの編集は、エンジンと接続する前から始められます。
パーティクル全体の見た目は、接続先エンジンのプレビューで確認します。

[![グラフ・編集パネル・再生コントロールを表示したLIBERA FXの制作画面](../images/editor/workspace.png)](../images/editor/workspace.png)

## 作りたいものから探す

| 作りたいもの・やりたいこと | 読むページ |
| --- | --- |
| 火花、噴水、画像を使う粒子 | [CPUパーティクル](cpu-particles.md) |
| GPU上で粒子を動かし、見た目を調整する | [GPUパーティクル](gpu-particles.md) |
| 粒子の画像をコマ送り・パーツ合成で動かす | [パーティクルのアニメーション](sprite-animation.md) |
| 煙、水、炎 | [Fluid2D](fluid-2d.md) |
| グラフ内で形状・画像・マテリアルを作る | [プロシージャル素材](procedural-resources.md) |
| 帯や、動きに沿って残る軌跡 | [帯・軌跡を作る](flexible-plane-draw.md) |
| 複数の演出を順番に動かす | [グラフ制作の基本](authoring.md)から[残像サンプル](afterimage-gate.md)へ |

## ゲームへ持ち込む

[接続からゲームでの再生まで](production-workflow.md)では、SDKの導入、素材の割り当て、
ライブプレビュー、書き出し、エディターなしでの再生を順に進めます。
素材の取得方法や接続方法はエンジンによって異なるので、使うエンジンの手順を選んでください。

組み込みコード、座標変換、インスタンスの準備、描画上の制約は[SDK・ランタイムの詳細](engine-integration.md)へ。
変換やゲームビルドを自動化するときは[一括変換・ビルド連携](batch-conversion.md)へ進みます。

## 知りたいこと・困ったことから探す

- 設定の場所を探す：[ウィンドウ・パネル一覧](editor-reference.md)
- ノードや言葉の意味を調べる：[標準ノード図鑑](node-reference.md)・[用語集](glossary.md)
- 表示されない、接続できない：[困ったときの確認](troubleshooting.md)
- 更新内容や対応条件を確認する：[リリースノート](release-notes.md)。エディターとSDKは同じリリースから導入します。
- AI クライアントを接続する：[MCP クライアント接続ガイド](mcp-guide.md)。

[Previewをダウンロード](https://github.com/Libra-Works/LibraFX/releases) ·
[ソースリポジトリ](https://github.com/Libra-Works/LibraFX) ·
[MITライセンス](https://github.com/Libra-Works/LibraFX/blob/main/LICENSE)

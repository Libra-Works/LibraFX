---
title: 導入と最初の起動
---

# 導入と最初の起動

ダウンロードから、編集できるサンプルを開くまでを進めます。
グラフを見るだけなら、エンジンの準備は後でも構いません。
粒子の描画も確認する場合は、使うエンジンのプレビューを用意します。

<span id="エディターを導入する"></span>

## 1. エディターを導入する

[GitHub Releases](https://github.com/Libra-Works/LibraFX/releases)から、使うOS向けの配布物を選びます。
エディターとSDKは**同じリリース**のものを使ってください。
既存の環境を更新する場合は、先に[リリースノート](release-notes.md)を確認します。

| 環境 | 起動方法 |
| --- | --- |
| Windows x64 | MSIで導入してスタートメニューから **LIBERA FX** を開くか、ポータブルZIPをすべて展開して直下の `LiberaFx.exe` を開きます。`Editor` と `Converter` は同じ場所に置きます。 |
| macOS・Apple Silicon | ZIPを展開し、`.app` を開きます。Previewのアプリは未署名・未公証です。 |
| Linux x64 | tarballを展開し、Avaloniaに必要なシステム依存関係が入ったデスクトップ環境で `Editor/LiberaFx` を起動します。 |

エンジンへのSDK導入は別の作業です。Unity・Godot同梱のnative binaryはWindows x64用で、
UnrealはソースプラグインのEditorビルドが必要です。[SDKの導入](engine-integration.md)で詳しく説明しています。

## 2. サンプルを開き、自分用のコピーを保存する

1. **File → Open** で、配布物の `Assets/Samples/CpuFountain.lfxproj` を開きます。
2. **File → Save As** で、自分の作業フォルダーへコピーを保存します。
3. ノードを選ぶと、インスペクターに値が表示されます。画面の場所や子グラフからの戻り方は、
   [画面の基本操作](workspace-tour.md)で順に試せます。

`.lfxproj` は編集元のプロジェクトです。後で書き出す `.lfxp` はゲームで読み込むデータなので、
編集元も残してください。

## 3. プレビューの方法を選ぶ

[接続からゲームでの再生まで](production-workflow.md)を開き、使うエンジンの手順を進めます。

| エンジン | 最初に使うもの |
| --- | --- |
| Unreal Engine | 専用の **LIBERA FX Preview** ウィンドウと **Connect to Libra**。 |
| Unity | SDKのエディタープレビューを有効にし、プロジェクトの素材を割り当てます。 |
| Godot | SDKのPlayerと、スクリプトによるプレビュー接続を使います。 |

接続表示だけでなく、エンジン側の画面にエフェクトが表示され、動くことを確認してください。
テクスチャー・メッシュ・マテリアルには、エンジン側での割り当ても必要です。

<span id="サンプルのグラフを読む"></span>
<span id="unreal-で描画を確認する"></span>

## 4. 最初の変更を試す

[最初の噴水を作る](first-fountain.md)へ進みます。
放出量・初速度・重力・大きさ・寿命を、どこで変更し、何を見比べるかを説明しています。
慣れたら、[残像サンプル](afterimage-gate.md)でSignalの遅延と子インスタンスの寿命を読んでみましょう。
表示されないときは、設定を一度に変える前に[困ったときの確認](troubleshooting.md)を使ってください。

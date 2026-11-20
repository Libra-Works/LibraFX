---
title: はじめに
---

[ドキュメントのトップ](index.md) · [ゲームエンジンとの連携](engine-integration.md)

# はじめに

「ノード」「ピン」「Signal」が初めての方は、[グラフの用語](glossary.md#graphs)を参照してください。

同梱の **Afterimage Gate** を開き、紫のリングと、その少し後に現れる水色の残像を
作る二つのシグナルをたどります。グラフを見るだけならゲームエンジンは不要です。
実際の描画を確認する場合は Unreal のプレビューを使います。

## エディターを導入する

[最新 Preview](https://github.com/Libra-Works/LibraFX/releases)
から、使っている OS に合うファイルをダウンロードします。
エディターと SDK は一緒に更新してください。
[移行手順](preview-update.md)に追加機能・パッケージ形式・既知の制約をまとめています。
導入からゲームへの組み込みまでは[エンジン別の制作フロー](production-workflow.md)、
無人変換・ビルド運用は[一括コンバーター](batch-conversion.md)を参照してください。

| プラットフォーム | 起動方法 |
| --- | --- |
| Windows x64 | MSI を実行してスタートメニューから **LIBERA FX** を開くか、ポータブル ZIP をすべて展開して直下の `LiberaFx.exe` を開きます。`Editor` と `Converter` は同じ場所に置いたままにします。 |
| macOS（Apple Silicon） | ZIP を展開してエディターの `.app` を開きます。アプリは署名・公証されていません。 |
| Linux x64 | tarball を展開し、Avalonia に必要なシステム依存関係が入ったデスクトップ環境で `Editor/LiberaFx` を起動します。 |

## サンプルのグラフを読む

1. エディターに同梱された `Assets/Samples/AfterimageGate.lfxproj` を開きます。
   編集を試す場合は、まず別名で保存します。
2. **Ignite** を見つけ、**Sequence** から出るシグナルをたどります。
   一方は紫のリングを生成し、もう一方は **Delay** で 0.55 秒待ってから水色の残像を生成します。
3. Spawnable Group を開き、**Time** と **Life Time** を確認します。
   各インスタンスの Time がアニメーションに使われ、Life Time が寿命の期限を決めます。
4. ノード、アセット、コマンドを探すときは、エニシング検索（既定は `Ctrl+P`）を使います。
   エディター UI と組み込みノード名の言語は別々に設定できます。

## Unreal で描画を確認する

検証用プロジェクトに [Unreal アダプター](engine-integration.md#unreal-engine) を導入します。
Unreal で **Window → LIBERA FX Preview** を開き、**Connect to Libra** を選びます。
LIBERA FX のタイムラインを 60 FPS、**End** を 144 に設定し、再生するか再生位置を動かします。
54〜63 フレーム付近で、紫のリングと水色の残像が同時に見えます。

ライブプレビューでは Unreal 標準の球メッシュと専用のアンリットマテリアルを使います。
書き出した `.lfxb` パッケージを単体で再生する場合は、明示的なアセット割り当てが必要です。
統合出力には更新後のエディターと SDK が必要です。旧リリースのパッケージ拡張子は `.lfxp` です。
プレビューに必要なネイティブライブラリやカメラ位置は
[同梱サンプルの説明（英語）](https://github.com/Libra-Works/LibraFX/blob/main/Assets/Samples/AfterimageGate.txt)
を参照してください。

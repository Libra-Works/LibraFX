---
title: ゲームエンジンとの連携
---

[ドキュメントのトップ](index.md) · [はじめに](getting-started.md)

# ゲームエンジンとの連携

[2026.11.8 Preview](https://github.com/Libra-Works/LibraFX/releases/tag/v2026.11.8)
から対応する SDK をダウンロードします。エンジンを閉じて ZIP を展開し、
以下に示すインストール用フォルダーだけをプロジェクトにコピーします。
各フォルダーに `VERSION.json` が入っています。

| SDK | プロジェクト内の配置先 | 同梱ランタイム |
| --- | --- | --- |
| LibraForUnity | `Packages/works.libra.liberafx` | Windows x64 ネイティブブリッジ |
| LibraForUnreal | `Plugins/LibraRuntimeForUnreal` | ポータブル Core を含むソースプラグイン |
| LibraForGodot | `addons/liberafx` | Windows x64 用 Godot 4.7 GDExtension |

更新前に既存のインストールをバックアップしてください。Unity では変更していない
アセットの `.meta` を残し、パッケージやネイティブプラグインの重複登録を解消します。
Godot では生成済みの `.gdextension.uid` を残します。Unreal では上書きで混ぜず、
インストール用フォルダーごと入れ替えます。開発用ジャンクションのリンク先は削除しないでください。

## 最初のエフェクトを再生する

最初は描画・GPU ノードを含まない CPU の Spawnable パッケージ（`.lfxp`）を使うと、
メッシュやマテリアルを割り当てる前に、読み込みと名前付き Game I/O を切り分けられます。
`.lfxproj` を保存し、`Converter` フォルダーに同梱された `LiberaFxConverter` で変換します。
Windows の例は次のとおりです。

```powershell
.\LiberaFxConverter.exe convert C:\Effects\input.lfxproj C:\Effects\effect.lfxp
```

エディターの **VFX を書き出す...** は `.lfxb` 用です。`.lfxp` の動作確認には、
名前付き Output Signal など、再生できたことが分かる出力をグラフに用意します。

### Unity

`.lfxp` を `Assets/` 以下に置き、インポートされたアセットを `LiberaFxSpawnableComponent`
に割り当てます。**Play On Enable** を有効にして Play Mode に入るか、`Play()` を呼びます。
開始に失敗した場合は `LastError` を確認し、出力は `GameIoEventReceived` で受け取ります。

### Unreal Engine

プラグインのコピー後にプロジェクトの Editor ターゲットをビルドします。
Content Browser で `.lfxp` をインポートし、アセットをレベルにドラッグすると
**Libra Effect** コンポーネント付きのアクターが配置されます。
**Auto Play** を有効にして Play In Editor を開始するか、`Play()` を呼びます。
出力は `OnGameIoEvent` で受け取ります。

### Godot

`.lfxp` を `res://` 以下に置き、`LibraEffectPlayer` を追加します。プレイヤーがシーンツリーに
入った後、`play_package(FileAccess.get_file_as_bytes("res://effect.lfxp"))` を呼びます。
戻り値 `0` は読み込み成功です。最初の処理フレームを待ってから入力シグナルを送り、
`game_io_event` で出力を確認します。ゲームと一緒に配布する場合は、エクスポートプリセットの
非リソースフィルターに `.lfxp` を含めます。

## エフェクトを拡張する

- **C# または GDScript:** ルートグラフに `action.*` という名前の Output Signal を用意し、
  Unity の `BindScriptAction` または Godot の `bind_script_action` で関数を登録します。
  関数はエンジンのメインスレッドで呼ばれ、戻り値は名前付き Input Property に渡して
  次の評価から使えます。描画には既存の Model Draw や Sprite ノードを使えます。
- **プロジェクト独自の C++ ノード:** ノード JSON の UUID、ABI、ポートに対応する
  ディスクリプターを登録します。Unreal ではモジュールの起動時に登録し、
  Unity・Godot ではネイティブブリッジに組み込みます。`.lfxp` のルートと子に対応します。
  同じ評価中に計算結果が必要な場合はこちらを使います。

登録手順、コード例、検証範囲は
[SDK 配布リファレンス（英語）](https://github.com/Libra-Works/LibraFX/blob/v2026.11.8/docs/sdk-distribution.md#project-defined-nodes-in-spawnable-packages)
を参照してください。同梱の Windows 用バイナリは、ほかのプラットフォーム、描画バックエンド、
コンソール SDK の動作を保証するものではありません。

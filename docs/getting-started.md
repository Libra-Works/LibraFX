# 入門

LIBERA FXは、値・Signal・寿命をグラフで組み合わせる作者用エディターです。
**エフェクトの実際の描画は接続先のUnity／Unrealで行います。**
グラフの画像、コンパイル成功、転送成功だけを「描画確認済み」とは扱いません。

1. 配布されたエディターと対応するランタイムSDKを用意します。
2. まず同梱の `Assets\Samples\Fireworks.lfxproj` を開き、
   発生側のSignal、Spawnable内の値、Life Timeを確認します。
3. プロジェクトを別名保存してから編集します。共有Groupをその場で更新したり、
   利用者の未保存文書を閉じたりする必要はありません。
4. 対応SDKを導入した専用エンジンプロジェクトを接続し、必要なアセットを
   プロジェクト固有の対応先へ割り当てます。素材の名前が同じでも自動一致はしません。
5. Previewを有効にしてTimelineを再生し、エンジン側の実際の描画とエラーを確認します。

細かな手順と対応範囲は[SDKの導入と配布](sdk-distribution.md)、
[Unity手順](../Runtime/Integrations/Unity/USAGE.txt)、
[Unreal手順](../Runtime/Integrations/Unreal/USAGE.txt)を参照してください。
Unity 7000系での過去の確認とUnity 6の確認は別です。Godotは未対応です。

編集はUndo／Redoできます。以前の履歴へ戻った後の編集は別の枝になるので、
別案を試すために元の案を破棄する必要はありません。MCP操作の入口は同梱の英語
[operator skill](../Skills/liberafx-operator/SKILL.md)です。

[寿命の考え方](lifetime.md)と[小さな実例](small-example.md)へ進んでください。

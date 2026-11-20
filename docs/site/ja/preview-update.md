---
title: 今回の Preview 更新と移行手順
---

# 今回の Preview 更新と移行手順

**2026.11.42 Preview** の内容を説明します。
[同じリリース](https://github.com/Libra-Works/LibraFX/releases/tag/v2026.11.42)
のエディターと SDK を組み合わせてください。それ以前のアーカイブは変更されず、
これらの機能は含まれません。

**Unreal の既知の問題：** 2026.11.42 同梱の Unreal プラグインには
プロシージャルモデルのソースラッパーが不足しており、ビルドに失敗することがあります。
Unreal をお使いの場合は、修正版の Preview を待ってから更新してください。

## 今回使えるようになること

| 機能 | 対応内容 |
| --- | --- |
| [2D の煙・水・炎](fluid-2d.md) | 煙、横から見る自由表面、上から見る浅水、燃料・温度による燃焼。リソース先頭の Flow、セル外力、専用描画、通常描画・別 GPU 粒子への生成出力参照。 |
| [プロシージャルリソース](procedural-resources.md) | モデル・テクスチャー・マテリアルの順序付き Flow、グラフ接続、生成 GPU 出力、同梱サンプル。 |
| [Spawnable のネストと CPU 粒子](cpu-particles.md#spawnable-の所有関係) | Spawnable の入れ子と内部 CPU システム、直近の親を参照する Capture/Live、個体ごとに独立した粒子。 |
| 寿命と終了処理 | Life Time でそのスコープの放出を停止し、粒子・子孫・予約処理が終わるまで維持。親の強制終了は配下だけに作用し、無関係な兄弟は残ります。 |
| [エンジン描画](engine-integration.md#rendering) | エンジンのアセットを使い、通常 Draw や CPU エミッターごとの描画 Component を直接描画・native primitive・RID に置換。 |
| 粒子の編集 | CPU の生成／更新／描画 Flow と、GPU の Point／Box／Sphere／Circle 放出形状。 |
| エディターの操作 | 枝分かれする編集履歴ツリー、ルートのエフェクト設定フライアウト、ノードのパラメーターをロック／解除する操作、グラフレイアウトの改善、拡大されたドックコンパス。 |

## 一緒に更新する

1. プロジェクトとエンジンのアセット割り当てをバックアップし、編集元の `.lfxproj` を残します。
2. エディター・コンバーター・SDK を**すべて更新**します。
   [エンジン導入手順](engine-integration.md)に従い、古い native library と新しい
   スクリプトを混在させたり、Unity のプラグインを重複登録したりしないでください。
3. 編集元から `.lfxb` を再出力します。ネストした所有関係やテンプレート内 CPU は
   **LFXP format 5 / package 13**、root のみの CPU は format 4 / package 12、
   通常のフルイドは GPU executable payload v2、生成リソース・描画参照と自動平面の明示指定は v3 を使います。
   フルイドシェーダーは 512 バイト uniform の対応版が必要です。
   旧 SDK は、内容を黙って省略せず非対応形式として拒否します。
4. 別の検証用プロジェクトで、読込・Play・自然終了・Stop・再生し直しを確認します。
   Unity の変更していない `.meta` と Godot の `.gdextension.uid` は維持してください。
   Unreal の配布プラグインはソース形式なので、プロジェクトの Editor target を再ビルドします。

Resource Identity 登録済み文書の通常 `.lfxb` 出力はまだ未対応で、`.lfxrm` は診断用です。
対応する経路は[制作フロー](production-workflow.md)、CPU 系に限る現在の CLI は
[一括変換](batch-conversion.md)を参照してください。
改善は [#71](https://github.com/Libra-Works/LibraFX/issues/71)・
[#72](https://github.com/Libra-Works/LibraFX/issues/72) で追跡します。

エディターの `build-info.json`、SDK の `VERSION.json` でビルド番号とソース版を確認できます。
対応する旧 `.lfxp` や raw `.lfxb` の読込は維持しますが、新しいパッケージのインポートだけで
旧 raw アセットやシーン内参照が自動移行するわけではありません。

## 確認した範囲

実装のローカル受入確認では、3エンジンとも実ピクセル、親・子の寿命後も生きる CPU 粒子、
終了時の解放、再生し直しを確認しました。フルイドは GPU フィールドと出力面も確認しています。

| エンジン | ローカルで確認した環境 |
| --- | --- |
| Unity | Windows の 7000 系、D3D11。今回のフルイド・ネスト CPU は Built-in fallback。通常描画は Built-in／URP の確認もあります。 |
| Unreal | Windows の UE 5.7、D3D11／SM5。Clang compile-only、既製エンジン向け MSVC リンク、実 RHI 自動テスト。 |
| Godot | Windows x64 の 4.7.2、Vulkan。通常版 GDExtension の実描画と寿命。 |

CI では Windows x64・macOS Apple Silicon・Linux x64 のエディターを配布用にビルドし、
Clang による共通 Core のテストとエンジン SDK のビルド・梱包確認を別途行います。
全 OS の UI、Unity 6、コンソール SDK、配布用 Player の受入保証ではありません。
macOS は未署名・未公証で、Unity／Godot 同梱 native binary は Windows x64 用です。

## まだ含まれないもの

3D フルイド、任意のリソース／パスの実行グラフ、エンジン主画面への書き込み、
固体障害物、浅水の地形水深、独立した飛沫粒子の形状は今後の対象です。
現在のセル外力は最大 8 個の順序付きノードで、任意のシェーダーグラフではありません。
同一スコープ内の生成出力参照は実装済みですが、親子スコープ間の共有は対象外です。

**3D より先に 2D を充実させる** [issue #69](https://github.com/Libra-Works/LibraFX/issues/69) の対応は完了しました。
確認済みの方式・環境・描画上の制限は[フルイドの説明](fluid-2d.md)を参照してください。
Niagara Fluids と同等の機能を揃えたという意味ではありません。
UE 5.8／5.9 の互換性は別途追跡しています。
Unity のシーン全体の半透明 Sprite 描画順にも制約が残ります。
詳細は[描画ガイド](engine-integration.md#rendering)を参照してください。

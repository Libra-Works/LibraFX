---
title: 今回の Preview 更新と移行手順
---

# 今回の Preview 更新と移行手順

**2026.11.22 Preview より後の更新**です。
[GitHub Releases](https://github.com/Libra-Works/LibraFX/releases) から、
**同じビルド番号**のエディターとエンジン SDK を組み合わせて導入してください。
過去に公開したアーカイブの内容は変わりません。

## 今回使えるようになること

| 機能 | 対応内容 |
| --- | --- |
| [2D の煙・インク](fluid-2d.md) | GPU の煙・色密度の移流、リソース先頭の Flow、固定の射影パス、密度テクスチャの描画。 |
| [Spawnable のネストと CPU 粒子](cpu-particles.md#spawnable-の所有関係) | Spawnable の入れ子と内部 CPU システム、直近の親を参照する Capture/Live、個体ごとに独立した粒子。 |
| 寿命と終了処理 | Life Time でそのスコープの放出を停止し、粒子・子孫・予約処理が終わるまで維持。親の強制終了は配下だけに作用し、無関係な兄弟は残ります。 |
| [エンジン描画](engine-integration.md#rendering) | エンジンのアセットを使い、通常 Draw や CPU エミッターごとの描画 Component を直接描画・native primitive・RID に置換。 |
| 粒子の編集 | CPU の生成／更新／描画 Flow と、GPU の Point／Box／Sphere／Circle 放出形状。 |
| エディターの操作 | ノード内アクションの整列と、編集中の階層に合わせたノード作成・検索の利用可否。 |

## 一緒に更新する

1. プロジェクトとエンジンのアセット割り当てをバックアップし、編集元の `.lfxproj` を残します。
2. エディター・コンバーター・SDK を**すべて更新**します。
   [エンジン導入手順](engine-integration.md)に従い、古い native library と新しい
   スクリプトを混在させたり、Unity のプラグインを重複登録したりしないでください。
3. 編集元から `.lfxb` を再出力します。ネストした所有関係やテンプレート内 CPU は
   **LFXP format 5 / package 13**、root のみの CPU は format 4 / package 12、
   フルイドは GPU executable payload v2 を使います。
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

煙・色密度は**液面・燃焼・3D フルイドではありません**。
任意ノードによる流体の力・乱流、Vorticity Confinement、汎用のシステム間テクスチャ参照、
フルイドを別 GPU パーティクルの発生素材にする連携は今後の対象です。
出力面の描画は実装済みですが、汎用リソースグラフや専用レンダラーへの受け渡しは未実装です。

**3D より先に 2D を充実させる**計画は
[issue #69](https://github.com/Libra-Works/LibraFX/issues/69) にまとめています。
Niagara Fluids と同等の機能を揃えたという意味ではありません。
Unity のシーン全体の半透明 Sprite 描画順にも制約が残ります。
詳細は[描画ガイド](engine-integration.md#rendering)を参照してください。

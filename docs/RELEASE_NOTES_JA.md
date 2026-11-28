# Prefab Variant Parent Replacer 1.0.0

VCC／ALCOMで管理できるVPM形式と、Unity Package Managerから導入できるTGZ形式で配布します。

- パッケージID：`dev.honomin.prefab-variant-parent-replacer`。
- 最小Unityバージョン：2022.3。Unity Editor専用で、MA・NDMF・VRChat SDKへの依存はありません。
- 既存の親差し替え・FileID照合・外部参照修復のC#処理は従来版と同一です。
- 標準のAssets配置にある旧スクリプトはVPMの移行対象です。別の場所へ配置したコピーは手動で取り除いてください。

導入・移行・操作手順は[README](https://github.com/honomin3/PrefabVariantParentReplacer#readme)を参照してください。

共通VPMリポジトリURL：

```text
https://raw.githubusercontent.com/honomin3/Packages/main/index.json
```

共通配布一覧への反映後、VCC／ALCOMでPrefab Variant Parent Replacerを選択できます。

添付ファイル：VPM用ZIP、UPM用TGZ、両ファイルのSHA-256を記載したSHA256SUMS。

本ツールはPrefabや参照元アセットのYAMLを直接書き換えます。利用前に必ずバックアップを取得し、READMEの免責事項と制限を確認してください。MIT License。

検証済み：パッケージ構成・収録ファイル・SHA-256・再生成時の一致・GitHub Actionsによる生成。

Unity 2022.3.22f1／Linuxの検証用プロジェクトで、UPM用TGZとVPM用ZIPの導入、Editor専用アセンブリの読み込み、メニュー起動、親差し替え、Override・GUID・外部参照の保持など、各17項目の確認が成功しました。[検証記録](https://github.com/honomin3/PrefabVariantParentReplacer/blob/master/docs/VALIDATION.md)に手順と結果を記載しています。

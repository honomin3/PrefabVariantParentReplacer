# 1.0.0のUnity動作確認

確認日：2026-11-28。環境：Linux x86_64、Unity **2022.3.22f1（887be4894c44）**、batchmode／nographics。VRChat SDK・MA・NDMFは未導入です。

同じ配布内容を、UPM用TGZとVPM用ZIPで別々の空プロジェクトへ導入し、各17項目の確認が成功しました。C#コンパイルエラーはありません。利用したPrefabとシーンは検証用に生成したデータです。

| 形式 | 導入・結果 |
| --- | --- |
| UPM | `UnityEditor.PackageManager.Client.Add` で配布TGZを追加。[結果JSON](validation/upm-result.json)。 |
| VPM | 配布ZIPの中身を `Packages/dev.honomin.prefab-variant-parent-replacer` へ配置。[結果JSON](validation/vpm-result.json)。 |

両方で以下を確認しています。

- Editor専用アセンブリの読み込みと、Toolsメニューからのウィンドウ起動。
- 事前解析で対象Variantと無関係なPrefabを区別すること。
- 指定した新しい親へ差し替わり、VariantのGUIDを維持すること。
- TransformとコンポーネントのOverrideを維持し、新しい親の値を継承すること。
- `.bak` を作成し、新旧の親・スキップしたPrefabのファイル内容を変更しないこと。
- 差し替え後もScriptableObjectとシーンからの参照が同じ要素へ解決されること。

各形式の実行ログでは親参照のFileIDを15件修正し、未解決0件、差し替え後に変化したFileIDを5件検出しました。実行したC#のSHA-256は、配布元のソースと一致しています。

```text
112acdf67fbe3447b47485bc6b6a9327b15dda5a4f6fc4290b6168535a585ec0
```

配布ZIPのSHA-256：

```text
6efc186c06c19821b484380682f51b4e4a195afda282cfd12ab56313072c1793
```

## 同じ確認を実行する

1. Unity 2022.3.22f1で、名前が **`pvpr-unity-validation`** の使い捨てプロジェクトを作成します。
2. 確認する形式の本パッケージを導入します。
3. リポジトリの `tests/unity/PvprSmoke.cs` と `PvprSmokeReferences.cs` を、そのプロジェクトの `Assets/Editor` へコピーします。検証コードは配布パッケージに含めていません。
4. プロジェクトを閉じ、次を実行します。Unityライセンスの有効化が必要です。

   ```bash
   unity run /path/to/pvpr-unity-validation --editor-version 2022.3.22f1 --timeout 240 --log-file /path/to/smoke.log -- -nographics -executeMethod PvprSmoke.Run
   ```

5. プロジェクト直下の `pvpr-validation-result.json` の `passed` と実行ログを確認します。テストは `Assets/PvprSmokeData` にデータを作成するため、再実行する場合は新しい使い捨てプロジェクトを使います。

これはUnity上の合成データによる確認です。VCC／ALCOMのGUI操作、Windows上の表示、実アバターや各外部ツールとの全組み合わせを検証済みとするものではありません。

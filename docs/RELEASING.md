# 配布ファイルの作成とVPM一覧への登録

配布構成はarmature-scale-syncと同じく `Packages/<パッケージID>` です。VPM一覧は [honomin3/Packages](https://github.com/honomin3/Packages) で管理します。

## 配布ファイルを作成する

1. `package.json` のバージョンと `CHANGELOG.md` を更新します。READMEを変更した場合はルート版とパッケージ版の内容を揃え、パッケージ内の相対リンクだけを調整します。
2. リポジトリのルートで次を実行します。Python 3.9以上が必要です。外部Pythonパッケージは不要です。

   ```bash
   python3 scripts/build_package.py
   ```

3. `dist` に以下が生成されます。ZIPは `package.json` が直下にあるVPM形式、TGZは `package/` 配下に同じ内容が入るUPM形式です。

   - `dev.honomin.prefab-variant-parent-replacer-<version>.zip`
   - `dev.honomin.prefab-variant-parent-replacer-<version>.tgz`
   - `SHA256SUMS`
   - `vpm-package.json`：共通VPM一覧へ追加するバージョン情報。単独のリポジトリ一覧ではありません。

4. Unity 2022.3のテスト用プロジェクトで、ZIPを展開してVCCのUser Packagesから導入できること、TGZをUPMから導入できること、メニューが開くことを確認します。従来の標準配置からのVPM移行と、別の場所にあるコピーを手動削除する移行も確認してください。
5. Prefabの親差し替え・外部参照修復はバックアップ済みのテストデータで確認します。パッケージ形式の検証だけでUnity上の動作確認を完了した扱いにはしません。

GitHub ActionsもPR・master更新・手動実行・配布タグのpushで同じ生成処理を行い、成果物を保存します。配布タグは `prefab-variant-parent-replacer-v<version>` です。例：`prefab-variant-parent-replacer-v1.0.0`。タグと `package.json` のバージョンが異なると生成に失敗します。

## 公開と共通VPM一覧への登録

1. `docs/RELEASE_NOTES_JA.md` を公開するバージョンの内容と検証結果へ更新し、公開するソースのコミットに上記のタグを付けてpushします。GitHub Actionsがパッケージを生成・検証し、ZIP・TGZ・`SHA256SUMS`・リリースノートを添付してGitHub Releaseを公開します。既存の下書きは更新できますが、公開済みの同じバージョンは上書きしません。`vpm-package.json` は登録作業用です。
2. `vpm-package.json` の `url` から公開済みZIPを取得できることと、取得したZIPのSHA-256が `zipSHA256` に一致することを確認します。ビルドし直した場合は、必ず実際に公開したファイルのハッシュを使用します。
3. `honomin3/Packages` の既存の `index.json` を開き、`packages["dev.honomin.prefab-variant-parent-replacer"].versions["<version>"]` に `vpm-package.json` のオブジェクトを追加します。既存のArmature Scale Syncや過去版、一覧の名前・ID・URLは保持します。共通一覧全体を `vpm-package.json` で置き換えないでください。
4. 初回公開時は、共通一覧のREADMEにも本リポジトリへのリンクを追加します。
5. 共通一覧を公開後、READMEに記載したVPMリポジトリURLをVCC／ALCOMへ登録し、本パッケージを追加できることを確認します。

初回リリースのZIP公開と共通一覧への登録が完了するまでは、VCC／ALCOMのオンライン一覧からインストールできません。先行確認にはローカルのUser PackagesかTGZを使います。

VPM形式と一覧の仕様は [VPM Packages](https://vcc.docs.vrchat.com/vpm/packages/) と [VPM Repos](https://vcc.docs.vrchat.com/vpm/repos/) を参照してください。Unity Editor専用ツールのため、MA・NDMF・VRChat SDKへの依存は追加しません。

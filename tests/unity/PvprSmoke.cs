using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;

// Run only in a disposable project named pvpr-unity-validation.
public static class PvprSmoke
{
    private const string Root = "Assets/PvprSmokeData";
    private static readonly List<string> Checks = new List<string>();

    [Serializable]
    private sealed class Report
    {
        public bool passed;
        public string unityVersion;
        public string[] checks;
        public string error;
    }

    public static void Run()
    {
        var report = new Report { unityVersion = Application.unityVersion };
        var code = 1;
        try
        {
            Check(new DirectoryInfo(Directory.GetCurrentDirectory()).Name == "pvpr-unity-validation",
                "Disposable validation project selected");
            Check(!AssetDatabase.IsValidFolder(Root), "Fixture folder does not already exist");
            Check(typeof(PrefabVariantParentReplacer).Assembly.GetName().Name ==
                  "Honomin.PrefabVariantParentReplacer.Editor", "Package Editor assembly loaded");
            EditorSettings.serializationMode = SerializationMode.ForceText;
            AssetDatabase.CreateFolder("Assets", "PvprSmokeData");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var oldParent = CreateParent("OldParent", Vector3.one);
            var newParent = CreateParent("NewParent", Vector3.one * 2);
            var unrelated = CreateParent("Unrelated", Vector3.one);
            var oldBytes = File.ReadAllBytes(Root + "/OldParent.prefab");
            var newBytes = File.ReadAllBytes(Root + "/NewParent.prefab");
            var unrelatedBytes = File.ReadAllBytes(Root + "/Unrelated.prefab");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(oldParent);
            instance.name = "Variant";
            var body = instance.transform.Find("Body");
            var expectedPosition = new Vector3(0, 0.25f, 0);
            var expectedSize = new Vector3(2, 3, 4);
            body.localPosition = expectedPosition;
            body.GetComponent<BoxCollider>().size = expectedSize;
            PrefabUtility.RecordPrefabInstancePropertyModifications(body);
            PrefabUtility.RecordPrefabInstancePropertyModifications(body.GetComponent<BoxCollider>());
            var variantPath = Root + "/Variant.prefab";
            var variant = PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
            UnityEngine.Object.DestroyImmediate(instance);
            var variantGuid = AssetDatabase.AssetPathToGUID(variantPath);
            Check(PrefabUtility.GetPrefabAssetType(variant) == PrefabAssetType.Variant,
                "Fixture is a Prefab Variant");

            var references = ScriptableObject.CreateInstance<PvprSmokeReferences>();
            references.Prefab = variant;
            references.Child = variant.transform.Find("Body").gameObject;
            references.Collider = references.Child.GetComponent<BoxCollider>();
            var referencePath = Root + "/References.asset";
            AssetDatabase.CreateAsset(references, referencePath);
            var holder = new GameObject("ReferenceHolder");
            var constraint = holder.AddComponent<ParentConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = references.Child.transform, weight = 1 });
            var scenePath = Root + "/References.unity";
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scenePath);
            AssetDatabase.SaveAssets();

            var options = new PrefabVariantParentReplacer.RebaseOptions
            {
                OldBasePrefab = oldParent,
                NewBasePrefab = newParent,
                TargetPrefabs = new List<GameObject> { variant, unrelated },
                RepairExternalReferences = true,
                CreateBackupFiles = true,
                ShowUI = false,
                ReloadModifiedOpenScenes = false,
                ExternalRepairRootAssetFolder = Root
            };
            var analysis = PrefabVariantParentReplacer.AnalyzeCore(options);
            Check(analysis.Succeeded && analysis.ValidTargetCount == 1 && analysis.SkippedTargetCount == 1,
                "Analysis identifies the variant and skips the unrelated prefab");
            var result = PrefabVariantParentReplacer.ExecuteRebaseCore(options);
            Check(result.Succeeded && result.RebasedPrefabCount == 1 && result.SkippedPrefabCount == 1,
                "Rebase succeeds and skips the unrelated prefab");
            var rebased = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
            Check(PrefabUtility.GetCorrespondingObjectFromSource(rebased) == newParent,
                "Variant parent changed to the new prefab");
            Check(AssetDatabase.AssetPathToGUID(variantPath) == variantGuid, "Variant GUID preserved");
            Check(rebased.transform.Find("Body").localPosition == expectedPosition,
                "Transform override preserved");
            Check(rebased.transform.Find("Body").GetComponent<BoxCollider>().size == expectedSize,
                "Component override preserved");
            Check(rebased.transform.Find("Body").localScale == Vector3.one * 2,
                "New parent values inherited");
            Check(File.Exists(variantPath + ".bak"), "Prefab backup created");
            Check(Equal(oldBytes, File.ReadAllBytes(Root + "/OldParent.prefab")) &&
                  Equal(newBytes, File.ReadAllBytes(Root + "/NewParent.prefab")), "Parent assets preserved");
            Check(Equal(unrelatedBytes, File.ReadAllBytes(Root + "/Unrelated.prefab")),
                "Skipped prefab preserved");

            AssetDatabase.ImportAsset(referencePath, ImportAssetOptions.ForceUpdate);
            references = AssetDatabase.LoadAssetAtPath<PvprSmokeReferences>(referencePath);
            Check(references.Prefab == rebased && references.Child == rebased.transform.Find("Body").gameObject &&
                  references.Collider == rebased.transform.Find("Body").GetComponent<BoxCollider>(),
                "ScriptableObject references resolve after rebase");
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            constraint = GameObject.Find("ReferenceHolder").GetComponent<ParentConstraint>();
            Check(constraint.GetSource(0).sourceTransform == rebased.transform.Find("Body"),
                "Scene reference resolves after rebase");
            Check(EditorApplication.ExecuteMenuItem("Tools/honomin3/Prefab Variant Parent Replacer"),
                "Tool menu opens");
            foreach (var window in Resources.FindObjectsOfTypeAll<PrefabVariantParentReplacer>())
                window.Close();
            report.passed = true;
            code = 0;
        }
        catch (Exception error)
        {
            report.error = error.ToString();
            Debug.LogException(error);
        }
        finally
        {
            report.checks = Checks.ToArray();
            File.WriteAllText("pvpr-validation-result.json", JsonUtility.ToJson(report, true));
            Debug.Log("PVPR_SMOKE_RESULT " + JsonUtility.ToJson(report));
            EditorApplication.Exit(code);
        }
    }

    private static GameObject CreateParent(string name, Vector3 bodyScale)
    {
        var root = new GameObject("Avatar");
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = bodyScale;
        body.AddComponent<BoxCollider>();
        var asset = PrefabUtility.SaveAsPrefabAsset(root, Root + "/" + name + ".prefab");
        UnityEngine.Object.DestroyImmediate(root);
        return asset;
    }

    private static bool Equal(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Checks.Add(description);
    }
}

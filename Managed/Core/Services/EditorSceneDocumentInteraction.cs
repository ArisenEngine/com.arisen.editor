using System.Threading.Tasks;
using Avalonia.Controls;
using ArisenEditorFramework.Utilities;
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;
using MsBox.Avalonia.Enums;

namespace ArisenEditor.Core.Services;

internal static class EditorSceneDocumentInteraction
{
    public static async Task<EditorSceneDocumentResult> SaveAsync(
        IEditorSceneDocumentService documentService,
        Window? owner = null)
    {
        var result = documentService.Save();
        if (!result.Success)
        {
            await ShowAsync(owner, "Scene Save Failed", result.Diagnostic, ButtonEnum.Ok, Icon.Error);
        }

        return result;
    }

    public static async Task<bool> TryOpenSceneAsync(
        IEditorSceneDocumentService documentService,
        AssetRef<SceneSourceAsset> scene,
        Window? owner = null)
    {
        var result = documentService.RequestOpenScene(scene);
        if (result.RequiresUserResolution)
        {
            if (!await ResolveUnsavedChangesAsync(
                    documentService,
                    "opening another scene",
                    owner))
            {
                return false;
            }

            result = documentService.RequestOpenScene(scene);
        }

        if (!result.Success)
        {
            await ShowAsync(owner, "Scene Open Failed", result.Diagnostic, ButtonEnum.Ok, Icon.Error);
            return false;
        }

        return true;
    }

    public static async Task<bool> ResolveUnsavedChangesAsync(
        IEditorSceneDocumentService documentService,
        string action,
        Window? owner = null)
    {
        var current = documentService.Current;
        if (current is not { IsDirty: true })
        {
            return true;
        }

        var choice = await ShowAsync(
            owner,
            "Unsaved Scene",
            $"Save changes to '{current.Name}' before {action}?\n\n" +
            "Yes saves, No discards the staged changes, and Cancel keeps the scene open.",
            ButtonEnum.YesNoCancel,
            Icon.Warning);

        EditorSceneDocumentResult result;
        switch (choice)
        {
            case ButtonResult.Yes:
                result = documentService.Save();
                break;
            case ButtonResult.No:
                result = documentService.DiscardChanges();
                break;
            default:
                return false;
        }

        if (result.Success)
        {
            return true;
        }

        await ShowAsync(owner, "Scene Operation Failed", result.Diagnostic, ButtonEnum.Ok, Icon.Error);
        return false;
    }

    private static Task<ButtonResult> ShowAsync(
        Window? owner,
        string title,
        string text,
        ButtonEnum buttons,
        Icon icon)
    {
        return owner == null
            ? MessageBoxUtility.ShowMessageBoxStandard(title, text, buttons, icon)
            : MessageBoxUtility.ShowMessageBoxStandard(owner, title, text, buttons, icon);
    }
}

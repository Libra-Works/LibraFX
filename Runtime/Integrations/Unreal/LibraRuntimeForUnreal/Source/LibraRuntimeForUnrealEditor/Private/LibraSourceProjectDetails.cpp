#include "LibraSourceProjectDetails.h"

#include "DetailCategoryBuilder.h"
#include "DetailLayoutBuilder.h"
#include "DetailWidgetRow.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformMisc.h"
#include "LibraSpawnableAsset.h"
#include "Misc/MessageDialog.h"
#include "Misc/Paths.h"
#include "Widgets/Input/SButton.h"

#define LOCTEXT_NAMESPACE "LibraSourceProjectDetails"

namespace {
bool IsAbsoluteLocalFilePath(const FString& path)
{
    if (path.IsEmpty() || path.StartsWith(TEXT("//"))) return false;
    for (auto index = 0; index < path.Len(); ++index)
        if (path[index] < 32 || path[index] == TEXT('"') || (path[index] == TEXT(':') && index != 1))
            return false;
#if PLATFORM_WINDOWS
    return path.Len() > 3 && FChar::IsAlpha(path[0]) && path[1] == TEXT(':') && path[2] == TEXT('/');
#else
    return path.StartsWith(TEXT("/")) && !path.Contains(TEXT(":"));
#endif
}

FText OpenProject(const FString& input)
{
    auto source = FString{};
    auto error = FText{};
    if (!LibraSourceProject::ValidatePath(input, source, error)) return error;
    auto executable = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBERAFX_EDITOR_EXECUTABLE"));
#if PLATFORM_WINDOWS
    FPaths::MakePlatformFilename(source);
    if (!executable.IsEmpty())
    {
        FPaths::NormalizeFilename(executable);
        if (!IsAbsoluteLocalFilePath(executable) ||
            !FPaths::GetExtension(executable).Equals(TEXT("exe"), ESearchCase::IgnoreCase) ||
            !IFileManager::Get().FileExists(*executable))
            return LOCTEXT("BadExe", "Set LIBERAFX_EDITOR_EXECUTABLE to the full local path of LiberaFx.exe and restart Unreal Editor.");
        FPaths::MakePlatformFilename(executable);
        // CreateProc starts the executable directly. The validated absolute
        // filename has no quotes/control characters and ends in .lfxproj.
        const auto args = FString::Printf(TEXT("\"%s\""), *source);
        auto handle = FPlatformProcess::CreateProc(*executable, *args, true, false, false,
            nullptr, 0, nullptr, nullptr);
        if (!handle.IsValid())
            return LOCTEXT("LaunchFailed", "Could not start LIBERA FX. Check that the configured executable can run, or open the source manually.");
        FPlatformProcess::CloseProc(handle);
        return {};
    }
#else
    if (!executable.IsEmpty())
        return LOCTEXT("AssociationOnly", "This platform uses its file association. Unset LIBERAFX_EDITOR_EXECUTABLE, restart Unreal Editor, and associate .lfxproj with LIBERA FX.");
#endif
#if PLATFORM_LINUX
    if (!IFileManager::Get().FileExists(TEXT("/usr/bin/xdg-open")))
        return LOCTEXT("MissingXdgOpen", "Install xdg-utils and associate .lfxproj with LIBERA FX, or open the source manually.");
#endif
    if (!FPlatformProcess::LaunchFileInDefaultExternalApplication(*source, nullptr, ELaunchVerb::Open, false))
        return LOCTEXT("NoAssociation", "Could not open the .lfxproj file. Associate .lfxproj with LIBERA FX or open the source manually. On Windows you can also set LIBERAFX_EDITOR_EXECUTABLE and restart Unreal Editor.");
    return {};
}
}

bool LibraSourceProject::ValidatePath(const FString& input, FString& fullPath, FText& error)
{
    fullPath.Reset();
    error = FText::GetEmpty();
    auto path = input;
    FPaths::NormalizeFilename(path);
    if (!IsAbsoluteLocalFilePath(path) ||
        !FPaths::GetExtension(path).Equals(TEXT("lfxproj"), ESearchCase::IgnoreCase))
    {
        error = LOCTEXT("BadSource", "Set Source Project Path to the original local .lfxproj file. Do not select the compiled import file.");
        return false;
    }
    if (!IFileManager::Get().FileExists(*path))
    {
        error = LOCTEXT("MissingSource", "The .lfxproj file is missing. Update Source Project Path to its current location.");
        return false;
    }
    fullPath = MoveTemp(path);
    return true;
}

void LibraSourceProject::AddDetails(IDetailLayoutBuilder& builder, TWeakObjectPtr<UObject> object)
{
    auto& category = builder.EditCategory("LIBERA FX Source", LOCTEXT("SourceCategory", "LIBERA FX Source"));
    category.AddCustomRow(LOCTEXT("OpenSearch", "Open LIBERA FX"))
        .WholeRowContent()
        [
            SNew(SButton)
            .Text(LOCTEXT("Open", "Open LIBERA FX"))
            .ToolTipText(LOCTEXT("OpenHelp", "Open the saved source project separately. This does not reimport or modify either file. If LIBERA FX is already running, open the source from that window."))
            .IsEnabled_Lambda([object] { return object.IsValid(); })
            .OnClicked_Lambda([object] {
                auto* package = Cast<ULibraSpawnableAsset>(object.Get());
                if (!package) return FReply::Handled();
                const auto error = OpenProject(package->SourceProjectPath.FilePath);
                if (!error.IsEmpty()) FMessageDialog::Open(EAppMsgType::Ok, error);
                return FReply::Handled();
            })
        ];
}


#undef LOCTEXT_NAMESPACE

# Ensure that changes are not pending.
$gitChanges = git status --porcelain;
$ChangedFiles = $($gitChanges | Measure-Object | Select-Object -expand Count)
if ($ChangedFiles -gt 0)
{
    Write-Output "There are $ChangedFiles uncommited changes in the repository. Must commit prior to tagging. Changed Files:"
    Write-Output $gitChanges
    Read-Host -Prompt "Press Enter to exit"
    exit
}

$projectFile = Get-ChildItem -filter "NexNet.props"
$version = Select-Xml -Path $projectFile -XPath "Project/PropertyGroup/Version" | Select-Object -ExpandProperty Node
$tag = "v" + $version.InnerText

$confirmation = Read-Host "Tag current commit with '$tag' (y/n):"
if ($confirmation -eq 'y') {
    git tag $tag
}

$confirmation = Read-Host "Push new tag to origin? (y/n):"
if ($confirmation -eq 'y') {
    git push origin $tag
    git push origin
}
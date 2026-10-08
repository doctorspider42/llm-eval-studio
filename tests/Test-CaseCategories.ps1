param([Parameter(Mandatory)][string]$BaseUrl)

$ErrorActionPreference = 'Stop'
$api = "$($BaseUrl.TrimEnd('/'))/api"
$created = [System.Collections.Generic.List[string]]::new()
$category = 'Category regression ' + [guid]::NewGuid().ToString('N')
function Request($method, $path, $body) {
    $args = @{ Method = $method; Uri = "$api/$path" }
    if ($null -ne $body) { $args.Body = ConvertTo-Json -InputObject $body -Depth 10; $args.ContentType = 'application/json' }
    Invoke-RestMethod @args | Write-Output
}
function Assert($condition, $message) { if (!$condition) { throw $message } }
try {
    $first = Request POST 'test-cases/' @{ title = 'Categorized case'; prompt = 'Test'; category = "  $category  "; tags = @('alpha') }
    $created.Add($first.id)
    Assert ($first.category -ceq $category) 'Category was not trimmed and returned'
    $second = Request POST 'test-cases/' @{ title = 'Uncategorized case'; prompt = 'Test'; tags = @('beta') }
    $created.Add($second.id)
    $encoded = [uri]::EscapeDataString($category)
    $filtered = @(Request GET "test-cases/?category=$encoded" $null)
    Assert ($filtered.Count -eq 1 -and $filtered[0].id -eq $first.id) 'Category filter failed'
    Assert (@(Request GET "test-cases/?category=$encoded&tag=beta" $null).Count -eq 0) 'Combined tag/category filter failed'
    Assert (@(Request GET 'test-cases/?uncategorized=true' $null).id -contains $second.id) 'Uncategorized filter failed'
    Assert (@(Request GET 'test-cases/categories' $null) -contains $category) 'Category list failed'

    $updated = Request PUT "test-cases/$($first.id)" @{ title = 'Edited'; prompt = 'Test'; category = $category; tags = @('beta') }
    Assert ($updated.category -eq $category -and $updated.tags -contains 'beta') 'Editing category failed'
    $count = Request PUT 'test-cases/category' @{ testCaseIds = @($first.id, $second.id, $first.id); category = $category }
    Assert ($count -eq 2) 'Bulk category assignment failed'
    Assert (@(Request GET "test-cases/?category=$encoded" $null).Count -eq 2) 'Bulk category was not persisted'

    $rows = @(@{ prompt = 'Imported A'; tags = 'source-a' }, @{ prompt = 'Imported B'; tags = 'source-b' })
    $dry = Request POST 'import/' @{ rows = $rows; category = $category; dryRun = $true }
    Assert ($dry.created -eq 0 -and @($dry.sample).Count -eq 2) 'Import preview failed'
    Assert (@($dry.sample | Where-Object { $_.category -ne $category }).Count -eq 0) 'Preview lost category'
    $imported = Request POST 'import/' @{ rows = $rows; category = $category }
    foreach ($id in $imported.createdIds) { $created.Add($id) }
    Assert ($imported.created -eq 2) 'Import creation failed'
    $filtered = @(Request GET "test-cases/?category=$encoded" $null)
    Assert ($filtered.Count -eq 4) 'Imported rows did not share a category'
    Assert (@(Request GET "test-cases/?category=$encoded&tag=source-a" $null).Count -eq 1) 'Import tags were not preserved'
    $null = Request PUT 'test-cases/category' @{ testCaseIds = @($first.id, $second.id); category = '  ' }
    Assert ($null -eq (Request GET "test-cases/$($first.id)" $null).category) 'Clearing category failed'
    Assert (@(Request GET "test-cases/?category=$encoded" $null).Count -eq 2) 'Clearing affected unrelated cases'

    foreach ($path in @('test-cases/', 'import/', 'test-cases/category')) {
        $body = switch ($path) {
            'test-cases/' { @{ title = 'Invalid'; prompt = 'Test'; category = ('x' * 121) } }
            'import/' { @{ rows = $rows; category = ('x' * 121) } }
            'test-cases/category' { @{ testCaseIds = @($first.id); category = ('x' * 121) } }
        }
        $response = Invoke-WebRequest -Method $(if ($path -eq 'test-cases/category') { 'PUT' } else { 'POST' }) -Uri "$api/$path" -ContentType 'application/json' -Body (ConvertTo-Json $body -Depth 10) -SkipHttpErrorCheck
        Assert ($response.StatusCode -eq 400) "Category length validation failed for $path"
    }
    Write-Output 'PASS: create, edit, category/tag filters, category listing, bulk assignment/clearing, import preview/persistence, and validation.'
}
finally {
    foreach ($id in $created) { $null = Request DELETE "test-cases/$id" $null }
}

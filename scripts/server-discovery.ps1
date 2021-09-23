Param(
	[Parameter(Mandatory = $true, Position = 1)]
    [ValidateNotNullOrEmpty()]
    [string]
    $ServerName = "DefaultNameIfNotProvided",

    [Parameter(Mandatory = $true, Position = 2)]
    [ValidateNotNullOrEmpty()]
    [string]
    $UserID
)
$output = @{UserID=$UserID;ServerName=$ServerName} | ConvertTo-Json -Compress
Write-Host $output
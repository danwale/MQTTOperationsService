Param(
	[Parameter(Mandatory = $true, Position = 1)]
    [ValidateNotNullOrEmpty()]
    [string]
    $ServerName = "DefaultNameIfNotProvided"
)
Write-Host "Secondary script ran with param ServerName param value '$ServerName'"
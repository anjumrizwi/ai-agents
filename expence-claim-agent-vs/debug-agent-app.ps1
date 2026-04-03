az login

az account set --subscription 333359c5-cc92-4749-8db5-60b6741fdb15
az account show --output table

az cognitiveservices account list --query "[].{name:name,rg:resourceGroup,kind:kind,location:location}" -o table

#prj-expence-claim-resource              rg-anjum.rizwi-2259         AIServices       eastus2

$accountName = "prj-expence-claim-resource"
$rg = "rg-anjum.rizwi-2259"

$endpoint = az cognitiveservices account show --name $accountName --resource-group $rg --query endpoint -o tsv
$endpoint

az cognitiveservices account deployment list --name $accountName --resource-group $rg -o table

#Name                    ResourceGroup
#----------------------  -------------------
#gpt-4o                  rg-anjum.rizwi-2259
#text-embedding-3-small  rg-anjum.rizwi-2259
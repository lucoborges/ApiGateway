# GatewayAPI

Gateway que recebe tarefas dos programas de origem e encaminha para os programas internos
(cadastro de produtos, fornecedores, clientes, datasheet e AribaSourcing).

## Como funciona

| Entrada | Destino | Configuração |
|---|---|---|
| `POST :5000/api/gateway/tarefa` com `tipoTarefa: PRODUTO` | `/api/produtos/cadastrar` | `Gateway:Servicos:Produto` |
| idem, `FORNECEDOR` | `/api/fornecedores/cadastrar` | `Gateway:Servicos:Fornecedor` |
| idem, `CLIENTE` | `/api/clientes/cadastrar` | `Gateway:Servicos:Cliente` |
| idem, `DATASHEET` | `/api/datasheet/processar` | `Gateway:Servicos:Datasheet` |
| idem, `ARIBA` | `/api/ariba/iniciar` | `Gateway:Ariba:ServicoUrl` |
| `POST :5001/api/ariba/iniciar`, `GET :5001/api/ariba/status` | AribaSourcing | `Gateway:Ariba:*` |

Exemplo de tarefa:

```json
{ "codigoTarefa": "T-123", "tipoTarefa": "PRODUTO", "dados": { } }
```

Outras rotas: `GET /health` (saúde e versão em execução), `/swagger` (documentação) e
`/api/tracking` (acompanhamento).

O gateway escuta na porta 5000 em todas as interfaces da máquina; a entrada do Ariba (5001)
só em `localhost`. Quem chama o gateway precisa apontar para o IP da máquina onde ele roda.

## Configuração

Tudo fica em `appsettings.json`, seção `Gateway`. Qualquer valor pode ser sobrescrito por variável
de ambiente trocando `:` por `__` (ex.: `Gateway__Servicos__Produto`).

- **`Servicos`**: endereço de cada programa de destino. Se um programa mudar de máquina, troque aqui.
- **`ApiKey`**: vazia = sem autenticação. Se definida, toda chamada precisa do cabeçalho `X-Api-Key`
  (exceto `/health` e `/swagger`). Defina pela variável de ambiente `Gateway__ApiKey` no servidor;
  não grave a chave no repositório. Antes de ativar, atualize todos os programas que chamam o gateway.
- **`ChaveCadastros`**: vazia = não envia. Se definida, o gateway envia esse valor no cabeçalho `X-Api-Key`
  de toda chamada para produtos, fornecedores e clientes (HSE-Cadastros); os outros destinos não recebem.
  Tem de ser igual à `Seguranca:ApiKey` do HSE-Cadastros. Defina pela variável de ambiente
  `Gateway__ChaveCadastros`. Para ativar: primeiro defina aqui e reinicie o gateway, depois ative no HSE-Cadastros.
- **`Swagger`**: `false` desliga a documentação.
- **`Logs:Pasta`**: onde gravar os logs. Vazio = pasta `logs`.

## Rodar na sua máquina

```bash
dotnet run
```

```bash
dotnet test tests/GatewayAPI.Tests
```

Os testes sobem o gateway em memória e trocam os programas de destino por um destino falso;
nenhuma chamada sai para a rede.

## Produção

- **Onde roda:** serviço do Windows `GatewayAPI`, em `C:\Services\GatewayAPI\app`, build de Release,
  ambiente Production. Sobe com o Windows e reinicia sozinho se cair; não depende de sessão aberta.
- **Logs:** `C:\Services\GatewayAPI\logs\gateway-AAAAMMDD.log`, um arquivo por dia, 30 dias.
- **Versão anterior:** `C:\Services\GatewayAPI\app-anterior`.

### Deploy

Todo push na `master` dispara `.github/workflows/deploy.yml`:

1. **Testes** em máquina do GitHub. Se falharem, nada chega ao servidor.
2. **Deploy** no servidor (runner `SERVIDOR2-GATEWAY`), pelo script `deploy/deploy.ps1`:
   publica a versão nova com o gateway ainda no ar, para o serviço, troca as pastas, inicia e
   confere `/health`. Se a versão nova não responder, a anterior é restaurada e o deploy termina com erro.

O gateway fica fora do ar por alguns segundos durante a troca. Pull requests rodam só os testes.

### Comandos úteis no servidor (PowerShell como administrador)

```powershell
Get-Service GatewayAPI                      # estado
Restart-Service GatewayAPI                  # reiniciar
Invoke-RestMethod http://localhost:5000/health   # saúde e versão
Get-Content C:\Services\GatewayAPI\logs\gateway-*.log -Tail 50   # últimos logs
```

Voltar manualmente para a versão anterior:

```powershell
Stop-Service GatewayAPI
Rename-Item C:\Services\GatewayAPI\app app-com-problema
Rename-Item C:\Services\GatewayAPI\app-anterior app
Start-Service GatewayAPI
```

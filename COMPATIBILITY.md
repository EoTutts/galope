# Correções Luma/dArkOS — 19/11/2026

## Fala

A distribuição anterior lia somente `/opt/a11y/etc/voz.conf`. O Luma atual e a SDK Mage Core leem a seção `voice` de `/roms/a11y/config.json`; por isso a voz do jogo podia divergir da configurada. Agora o jogo usa essa seção, mantém voz/módulo/velocidade/tom/volume, atualiza a configuração antes de cada fala e respeita FN + Y e o histórico de falas. O leitor JSON aceita arquivo compacto e caracteres escapados, sem misturar outras seções. `A11Y_CONFIG` permite um caminho alternativo; `A11Y_VOZ_CONF` explícito e o fallback legado continuam disponíveis. O idioma selecionado mantém a adaptação já existente para as vozes disponíveis.

Não há processo externo de TTS por mensagem. O transporte continua sendo SSIP pelo socket do Speech Dispatcher, com cancelamento somente do próprio cliente e os callbacks/fila existentes.

## Particularidades deste jogo

O frontend também deixa de selecionar o socket legado `/tmp/r36s-speechd.sock` quando `A11Y_CONTRACT=1`; respeita o endereço fornecido pelo Luma. O mixer próprio, streaming, controles e salvamentos foram mantidos. O overlay recompila o frontend a partir do código acompanhado aqui, sem trocar áudio ou imagens do pacote base.

## Fontes consultadas

- Luma (`gogabs/r36s-a11y` redireciona para `gogabs/luma`): `bee77ada902bebfc4ddfb3c7cf1eecec0f58c327`.
- SDK Mage Core: `e51b0f1deeb33c4f2b7d90e86a23c8e1b4971423`.
- Dead vs. Alive: `42b9dff2a2878ccfbd1952fc925c5762d98f7681`.
- Comparação com o adaptador público de fala do Stelar Flow e os exemplos/contrato da SDK. Consulta autenticada pelo GitHub CLI; sem falha de acesso aos repositórios.

## Construir o pacote completo

Requer Python 3.8+ e, nos jogos C#, .NET SDK 10. Baixe o ZIP anterior indicado em `PACKAGE.json` como `base.zip`. Execute:

```
python compat/build_overlay.py
python make_package.py base.zip overlay galope-linux-arm64.zip
```

O construtor confere o SHA-256 do pacote anterior, preserva os demais arquivos e registra os hashes das alterações em `COMPATIBILITY-MANIFEST.json`. A instalação deve usar o ZIP completo pela Página no PC; o overlay isolado não é um pacote instalável. IDs e diretórios de salvamentos permanecem os mesmos.

## Validação e limites

Testes de contrato verificam a configuração de voz, mudança durante o jogo, mute, callbacks e cancelamento local. Os testes C# usam um servidor SSIP de teste por socket Unix; o teste Python simula o transporte. A CI acompanha esta revisão; seus resultados devem ser lidos no PR. Não foi possível acessar o console por SSH nesta sessão (expirou), e o console do amigo não foi acessado. Portanto não há confirmação física desta revisão no dArkOS nem avaliação auditiva humana. Após instalar, comparar a mesma voz na galeria e no jogo; alterar velocidade, testar FN + Y e sair para a galeria.

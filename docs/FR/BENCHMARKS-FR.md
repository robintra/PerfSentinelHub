# Mesures

Ce que pèse l'image publiée, en combien de temps elle répond après un
démarrage à froid, et combien de mémoire elle occupe au repos. Chaque chiffre
ci-dessous vient de l'image publiée `ghcr.io/robintra/perf-sentinel-hub:0.3.3`,
pas d'un build local, et les commandes pour les reproduire terminent la page.

Le Hub est publié en NativeAOT : du C# 14 sur .NET 10 compilé à l'avance en un
seul exécutable natif, sans runtime .NET ni JIT dans l'image. La base est
`runtime-deps` chiselée, qui n'embarque que les bibliothèques système, et le
processus tourne sous l'utilisateur non root `1654`.

## En bref

| Chiffre                             | Valeur                                                                      |
|-------------------------------------|-----------------------------------------------------------------------------|
| Démarrage à froid, première réponse | 130 ms en médiane, de 120 à 138 ms sur 10 essais                            |
| Mémoire au repos                    | 20,3 à 20,8 Mio (21,3 à 21,8 Mo), 60 s après le démarrage, sur 3 conteneurs |
| Image compressée, amd64             | 39,8 Mo : base 21,4, Hub 11,1, Perf Sentinel 7,4                            |
| Image compressée, arm64             | 37,7 Mo : base 20,3, Hub 10,6, Perf Sentinel 6,8                            |

## Ce que contient l'image

L'image embarque le binaire du moteur Perf Sentinel, que le lanceur exécute en
sous-processus. Le détail ci-dessous sépare ce qui relève du Hub de ce qui
relève du moteur et de la base.

| Couche                     | amd64, compressé | arm64, compressé | arm64, sur disque |
|----------------------------|------------------|------------------|-------------------|
| Base chiselée              | 21,4 Mo          | 20,3 Mo          |                   |
| Exécutable du Hub          | 9,6 Mo           | 9,1 Mo           | 21,1 Mo           |
| Bibliothèque native SQLite | 0,7 Mo           | 0,8 Mo           | 1,5 Mo            |
| Interface du navigateur    | 0,8 Mo           | 0,8 Mo           |                   |
| Perf Sentinel 0.25.2       | 7,4 Mo           | 6,8 Mo           | 15,2 Mo           |

En amd64, une fois décompressés, les exécutables pèsent 20,4 Mo pour le Hub et
19,1 Mo pour le moteur.

## Conditions

- Machine : Apple M4 Pro, Docker 29.8.0, variante arm64 de l'image.
- Une source daemon configurée vers un port fermé, pour que chaque poll échoue
  vite et que rien ne soit jamais importé. La base reste vide.
- `/data` est un tmpfs appartenant à l'UID `1654`.
- Le démarrage à froid est chronométré du `docker run` au premier `200` sur
  `/`. Il inclut donc le démarrage du conteneur en plus de celui du Hub.
- La mémoire est le chiffre du conteneur donné par `docker stats`, hors cache
  de pages.
- Les tailles d'image sont celles des couches, lues dans le manifeste du
  registre pour chaque architecture.

## Ce que ces chiffres ne disent pas

Le chiffre de mémoire est un plancher. Un Hub qui importe des findings, garde
des jours d'observation, sert le lanceur et produit des rapports en occupe
davantage, et cette page n'en donne aucun chiffre tant qu'il n'a pas été mesuré
sur une vraie flotte. Les chiffres de démarrage et de mémoire valent pour
arm64 seulement. L'image amd64 a été pesée, pas exécutée.

## Reproduire

```bash
IMG=ghcr.io/robintra/perf-sentinel-hub:0.3.3
docker pull "$IMG"

docker run -d --name hub -p 18080:8080 \
  --tmpfs /data:uid=1654,gid=1654 \
  -e Hub__Sources__0__Id=bench \
  -e Hub__Sources__0__Name=Bench \
  -e Hub__Sources__0__Environment=test \
  -e Hub__Sources__0__BaseUrl=http://127.0.0.1:9 \
  -e Hub__Sources__0__ImportApiKey="$(openssl rand -hex 16)" \
  "$IMG"

until [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18080/)" = 200 ]; do sleep 0.005; done

sleep 60
docker stats --no-stream --format '{{.MemUsage}}' hub
docker rm -f hub
```

Chronométrer ensemble le `docker run` et la boucle d'attente donne le démarrage
à froid. Les tailles de couche viennent du manifeste de chaque plateforme de
l'index d'image.

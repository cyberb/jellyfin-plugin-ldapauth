[{
    kind: "pipeline",
    type: "docker",
    name: "amd64",
    platform: {
        os: "linux",
        arch: "amd64"
    },
    steps: [
    {
        name: "build",
        image: "mcr.microsoft.com/dotnet/sdk:6.0-bullseye-slim",
        commands: [
            "dotnet publish -c Release -o out/LDAP-Auth",
            "cp meta.json out/LDAP-Auth",
            "tar czf LDAP-Auth.tar.gz -C out LDAP-Auth"
        ],
        volumes: [
            {
                name: "shm",
                path: "/dev/shm"
            }
        ]
    },
    {
            name: "publish to github",
            image: "plugins/github-release:1.0.0",
            settings: {
                api_key: {
                    from_secret: "github_token"
                },
                files: "LDAP-Auth.tar.gz",
                overwrite: true,
                file_exists: "overwrite"
            },
            when: {
                event: [ "tag" ]
            },
        }
],
    volumes: [
        {
            name: "shm",
            temp: {}
        }
    ]
}]

# RabbitMQ setup and migration

Both Compose profiles publish AMQP (5672) and management (15672) on `127.0.0.1`.
Container clients use `messaging:5672`; host launchers use `127.0.0.1:5672` or an
SSH tunnel. Do not change the host bind to allow a remote launcher; tunnel it.

Before starting Compose, export `RABBITMQ_DEFAULT_USER` (for example `ows-launcher`)
and `RABBITMQ_DEFAULT_PASS` (a unique randomly generated password), or put them in
a private, untracked `--env-file`. Both are required, including for local development.
Never save the password in the tracked `src/.env` or appsettings files.

Compose supplies the same credentials to the broker and container clients. For a
launcher or API running directly on the host, supply
`RabbitMQOptions__RabbitMQUserName` and `RabbitMQOptions__RabbitMQPassword` through
its process environment or .NET user secrets. Compose's `.env` does not configure
host .NET processes. Set its broker host to `127.0.0.1` (the appsettings default).

The application user has no management tags and only launcher resource permissions.
It cannot log into the management UI/API. For routine diagnostics use
`docker compose exec messaging rabbitmq-diagnostics -q ping` and
`docker compose exec messaging rabbitmqctl list_users`. Use your profile's normal
`-f` and `--env-file` options on all Compose commands. If management UI access is
needed, provision a separate operator account locally and reach it via loopback or
an SSH tunnel. Do not grant the application account administrator access.

## Existing volumes

RabbitMQ's bootstrap settings apply only to an empty broker database, as described
in [RabbitMQ access control](https://www.rabbitmq.com/docs/access-control).
Recreating a container does **not** rotate the old `dev/test` account or its permissions.
Do not delete a volume containing queued commands to apply this change.

1. Stop launchers and command publishers during migration. Set a new application
   username (different from `dev`) and password in your private environment file,
   then recreate the messaging container so it receives those variables and the
   loopback port bindings. Keep the existing data volume.
2. Create and restrict the new user using the container's supplied environment
   (the following commands use POSIX shell quoting on the host):

   ```sh
   docker compose exec messaging sh -c 'rabbitmqctl add_user "$RABBITMQ_DEFAULT_USER" "$RABBITMQ_DEFAULT_PASS"'
   docker compose exec messaging sh -c 'rabbitmqctl set_user_tags "$RABBITMQ_DEFAULT_USER"'
   docker compose exec messaging sh -c 'rabbitmqctl set_permissions -p / "$RABBITMQ_DEFAULT_USER" "^ows\.(serverspinup|servershutdown)(\.[0-9]+\.launcher)?$" "^ows\.(serverspinup|servershutdown)(\.[0-9]+\.launcher)?$" "^ows\.(serverspinup|servershutdown)(\.[0-9]+\.launcher)?$"'
   ```

3. Configure all host launchers with the new credentials and recreate container
   clients. Remove the old account with `docker compose exec messaging rabbitmqctl
   delete_user dev`. Review `list_users` and `list_permissions -p /` for other old
   accounts, including `guest`. Verify the new user has an empty tag list and only
   the launcher patterns. Resume publishers and launchers and verify spin-up/shutdown.

## Verification

- `docker port RabbitMQ` for the base stack must list only `127.0.0.1` bindings for
  5672 and 15672. For Hetzner, use `docker compose port messaging 5672` and `15672`.
- From another LAN host, `curl --connect-timeout 3 -u dev:test
  http://<docker-host>:15672/api/whoami` must fail to connect. Check AMQP 5672 from
  that host too. This requires a second machine and a running Docker engine.
- With the password unset/empty, both profiles must reject `docker compose config`
  before creating containers.
- On a fresh volume, inspect `rabbitmqctl list_users` and `list_permissions -p /`;
  the supplied application user must have no tags and only launcher permissions.
  Verify real launcher delivery before retiring any old account in an existing installation.

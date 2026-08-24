<script setup lang="ts">
    import { reactive, onMounted } from 'vue';
    import { useRoute } from 'vue-router';
    import owsApi from '../owsApi';

    interface CharacterRow {
        characterID: number,
        userGUID: string | null,
        charName: string,
        email: string,
        characterLevel: number,
        mapName: string | null,
        className: string,
        isAdmin: boolean,
        isModerator: boolean,
        isInternalNetworkTestUser: boolean,
        lastActivity: string
    }

    interface Data {
        headers: Array<object>,
        rows: Array<CharacterRow>,
        search: string,
        loading: boolean,
        savingCharacterID: number,
        message: string,
        messageType: string,
        scopedToUser: string
    }

    const data: Data = reactive({
        headers: [
            { title: 'Character', align: 'start', key: 'charName' },
            { title: 'Owner', key: 'email' },
            { title: 'Class', key: 'className' },
            { title: 'Level', key: 'characterLevel' },
            { title: 'Zone', key: 'mapName' },
            { title: 'Admin', key: 'isAdmin', sortable: false },
            { title: 'Moderator', key: 'isModerator', sortable: false },
            { title: 'Net Test', key: 'isInternalNetworkTestUser', sortable: false },
            { title: 'Last Activity', key: 'lastActivity' }
        ],
        rows: [],
        search: '',
        loading: false,
        savingCharacterID: 0,
        message: '',
        messageType: 'success',
        scopedToUser: ''
    });

    const route = useRoute();

    function applyResponse(response: any) {
        data.rows = Array.isArray(response.data) ? response.data : [];
    }

    function loadCharacters() {
        data.loading = true;
        data.message = '';

        const userGuid = route.query.userGuid as string | undefined;
        const request = userGuid
            ? owsApi.getCharactersForUser(userGuid)
            : owsApi.searchCharacters(data.search);

        data.scopedToUser = userGuid ?? '';

        request.then(applyResponse).catch((error: any) => {
            data.messageType = 'error';
            data.message = `Could not load characters: ${error?.message ?? 'unknown error'}`;
        }).finally(function () {
            data.loading = false;
        });
    }

    function clearUserScope() {
        // Drop the ?userGuid filter and fall back to the name/email search.
        window.location.href = '/characters';
    }

    type FlagField = 'isAdmin' | 'isModerator' | 'isInternalNetworkTestUser';

    const FLAG_LABELS: Record<FlagField, string> = {
        isAdmin: 'admin',
        isModerator: 'moderator',
        isInternalNetworkTestUser: 'network test user'
    };

    function saveFlags(row: CharacterRow, field: FlagField, value: boolean) {
        const previous = row[field];
        row[field] = value;
        data.savingCharacterID = row.characterID;
        data.message = '';

        // Send only the flag that changed. The API leaves omitted flags alone, so two
        // people editing different flags on the same character cannot clobber each other.
        const payload: Record<string, unknown> = { characterID: row.characterID };
        payload[field] = value;

        owsApi.setCharacterFlags(payload).then((response: any) => {
            if (response.data && response.data.success) {
                data.messageType = 'success';
                const effect = field === 'isInternalNetworkTestUser'
                    ? 'Applies on the next connect: the server will hand this character 127.0.0.1 instead of the zone server IP.'
                    : 'Takes effect the next time that character logs in.';
                data.message = `${row.charName}: ${FLAG_LABELS[field]} ${value ? 'on' : 'off'}. ${effect}`;
            }
            else {
                row[field] = previous;
                data.messageType = 'error';
                data.message = response.data?.errorMessage || 'The server refused the change.';
            }
        }).catch((error: any) => {
            row[field] = previous;
            data.messageType = 'error';
            data.message = `Could not save: ${error?.message ?? 'unknown error'}`;
        }).finally(function () {
            data.savingCharacterID = 0;
        });
    }

    onMounted(() => {
        loadCharacters();
    });
</script>

<template>
<v-container>
    <div class="characters-container">
        <v-data-table :headers="data.headers"
                      :items="data.rows"
                      :loading="data.loading"
                      :items-per-page="10"
                      class="elevation-1">

            <template v-slot:top>
                <v-toolbar flat>
                    <v-toolbar-title>Characters</v-toolbar-title>
                    <v-divider class="mx-4" inset vertical></v-divider>
                    <v-text-field v-if="!data.scopedToUser"
                                  v-model="data.search"
                                  label="Search by character name or owner email"
                                  density="compact"
                                  hide-details
                                  single-line
                                  clearable
                                  @keyup.enter="loadCharacters"></v-text-field>
                    <v-chip v-else class="ml-4" closable @click:close="clearUserScope">
                        Showing one user's characters
                    </v-chip>
                    <v-spacer></v-spacer>
                    <v-btn rounded="pill" color="primary" @click="loadCharacters">
                        <v-icon icon="mdi-magnify"></v-icon> Search
                    </v-btn>
                </v-toolbar>

                <v-alert v-if="data.message" :type="data.messageType as any" density="compact" class="ma-2">
                    {{ data.message }}
                </v-alert>
            </template>

            <template v-slot:item.isAdmin="{ item }">
                <v-switch :model-value="item.raw.isAdmin"
                          color="error"
                          density="compact"
                          hide-details
                          :disabled="data.savingCharacterID === item.raw.characterID"
                          @update:modelValue="() => saveFlags(item.raw, 'isAdmin', !item.raw.isAdmin)"></v-switch>
            </template>

            <template v-slot:item.isModerator="{ item }">
                <v-switch :model-value="item.raw.isModerator"
                          color="warning"
                          density="compact"
                          hide-details
                          :disabled="data.savingCharacterID === item.raw.characterID"
                          @update:modelValue="() => saveFlags(item.raw, 'isModerator', !item.raw.isModerator)"></v-switch>
            </template>

            <template v-slot:item.isInternalNetworkTestUser="{ item }">
                <v-switch :model-value="item.raw.isInternalNetworkTestUser"
                          color="info"
                          density="compact"
                          hide-details
                          :disabled="data.savingCharacterID === item.raw.characterID"
                          @update:modelValue="() => saveFlags(item.raw, 'isInternalNetworkTestUser', !item.raw.isInternalNetworkTestUser)"></v-switch>
            </template>

            <template v-slot:no-data>
                <div class="pa-4">No characters matched. Search results are capped at 200 rows.</div>
            </template>
        </v-data-table>

        <v-alert type="info" density="compact" style="margin-top: 24px;">
            <strong>Admin</strong> and <strong>Moderator</strong> set
            <code>Characters.IsAdmin</code> / <code>IsModerator</code>, which the game client reads
            once at login. The server does not check them, so any power they unlock needs its own
            server-side check.
        </v-alert>

        <v-alert type="warning" density="compact" style="margin-top: 12px;">
            <strong>Net Test</strong> sets <code>Characters.IsInternalNetworkTestUser</code>, and
            this one the server does act on: <code>GetServerToConnectTo</code> hands the character
            <code>127.0.0.1</code> rather than the zone server's real IP. Only useful when the
            player is on the same machine as the zone server - for anyone else it makes the game
            unjoinable until it is switched back off.
        </v-alert>
    </div>
</v-container>
</template>

<style scoped>
    .characters-container {
        margin-top: 0px;
    }
</style>

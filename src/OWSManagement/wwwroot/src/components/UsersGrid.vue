<script setup lang="ts">
    import { reactive, onMounted } from 'vue';
    import UsersAdd from "./UsersAdd.vue";
    import owsApi from '../owsApi';
    import router from '../router';

    interface Data {
        headers: Array<object>,
        rows: Array<Record<string, any>>,
        roles: Array<string>,
        showEditingUserDialog: boolean,
        editUser: Record<string, any>,
        editUserIndex: number,
        addingANewUser: boolean,
        search: string,
        loading: boolean,
        savingUserGuid: string,
        message: string,
        messageType: string
    }

    const data: Data = reactive({
        headers: [
            { title: 'Actions', sortable: false, align: 'start', key: 'actions' },
            { title: 'First Name', align: 'start', key: 'firstName', },
            { title: 'Last Name', key: 'lastName' },
            { title: 'Email', key: 'email' },
            { title: 'Steam ID', key: 'steamId' },
            { title: 'Role', key: 'role' },
            { title: 'Net Test', key: 'networkTestCharacterCount', sortable: false }
        ],
        rows: [],
        roles: ['Player', 'Moderator', 'GameMaster', 'Admin'],
        showEditingUserDialog: false,
        editUser: {},
        editUserIndex: -1,
        addingANewUser: false,
        search: '',
        loading: false,
        savingUserGuid: '',
        message: '',
        messageType: 'success'
    });

    function loadUsersGrid() {
        data.loading = true;
        data.message = '';

        owsApi.searchUsers(data.search).then((response: any) => {
            data.rows = Array.isArray(response.data) ? response.data : [];
        }).catch((error: any) => {
            data.messageType = 'error';
            data.message = 'Could not load users: ' + (error?.message ?? 'unknown error');
        }).finally(function () {
            data.loading = false;
        });

        owsApi.getRoles().then((response: any) => {
            if (Array.isArray(response.data) && response.data.length > 0) {
                data.roles = response.data;
            }
        }).catch(() => {
            // Fall back to the hardcoded list; not worth surfacing.
        });
    }

    function clickAddNewUser() {
        data.addingANewUser = true;
    }

    function editUser(userToEdit: Record<string, unknown>) {
        data.editUserIndex = data.rows.indexOf(userToEdit);
        data.editUser = Object.assign({}, userToEdit);
        data.showEditingUserDialog = true;
    }

    function editUserSave() {
        owsApi.updateUser(data.editUser).then((response: any) => {
            if (response.data && response.data.success) {
                data.rows[data.editUserIndex] = data.editUser;
                data.showEditingUserDialog = false;
                data.messageType = 'success';
                data.message = 'Saved ' + data.editUser.email + ' as ' + data.editUser.role + '.';
            }
            else {
                data.messageType = 'error';
                data.message = response.data?.errorMessage || 'Unable to update the user.';
            }
        }).catch((error: any) => {
            data.messageType = 'error';
            data.message = 'Could not save: ' + (error?.message ?? 'unknown error');
        });
    }

    function editUserClose() {
        data.showEditingUserDialog = false;
    }

    // The flag lives on Characters, so an account is "on" only when every character has it.
    // A partial state (some characters flagged from the Characters page) shows as off with a
    // count, and switching on brings the rest into line.
    function netTestState(user: Record<string, any>) {
        const total = user.characterCount ?? 0;
        const flagged = user.networkTestCharacterCount ?? 0;
        return {
            total,
            flagged,
            all: total > 0 && flagged === total,
            partial: flagged > 0 && flagged < total
        };
    }

    function setNetTest(user: Record<string, any>, value: boolean) {
        data.savingUserGuid = user.userGUID;
        data.message = '';

        owsApi.setUserNetworkTestFlag({
            userGUID: user.userGUID,
            isInternalNetworkTestUser: value
        }).then((response: any) => {
            if (response.data && response.data.success) {
                user.networkTestCharacterCount = value ? (user.characterCount ?? 0) : 0;
                data.messageType = 'success';
                data.message = response.data.errorMessage
                    ? `${user.email}: ${response.data.errorMessage}`
                    : `${user.email}: network test ${value ? 'on' : 'off'} for all `
                      + `${user.characterCount} character(s). Applies on their next connect.`;
            }
            else {
                data.messageType = 'error';
                data.message = response.data?.errorMessage || 'Unable to set the flag.';
            }
        }).catch((error: any) => {
            data.messageType = 'error';
            data.message = 'Could not save: ' + (error?.message ?? 'unknown error');
        }).finally(function () {
            data.savingUserGuid = '';
        });
    }

    function viewCharacters(user: Record<string, any>) {
        // AdminUserSummary.UserGUID serialises as userGUID, not userGuid as the old User
        // model did. The route's own query key stays userGuid; CharactersGrid reads that.
        router.push({ path: '/characters', query: { userGuid: user.userGUID } });
    }

    function deleteUser(userToDelete: Record<string, unknown>) {
        if (confirm("Are you sure you want to remove the player: " + userToDelete.firstName + " " + userToDelete.lastName)) {
            alert("Delete the user.  Not implemented yet!");
        }
    }

    onMounted(() => {
        loadUsersGrid();
    });
</script>

<template>
<v-container>
    <div class="users-container">
        <div v-if="data.addingANewUser">
            <UsersAdd :roles="data.roles" />
        </div>
        <div v-else>
            <div>
                <v-data-table :headers="data.headers"
                              :items="data.rows"
                              :loading="data.loading"
                              :items-per-page="10"
                              class="elevation-1 users-table">

                    <template v-slot:top>
                        <v-toolbar flat>
                            <v-toolbar-title>Users</v-toolbar-title>
                            <v-divider class="mx-4"
                                       inset
                                       vertical></v-divider>
                            <v-text-field v-model="data.search"
                                          label="Search by email, name or Steam ID"
                                          density="compact"
                                          hide-details
                                          single-line
                                          clearable
                                          @keyup.enter="loadUsersGrid"></v-text-field>
                            <v-btn rounded="pill"
                                   color="primary"
                                   class="ml-2"
                                   style="margin-left:8px;"
                                   @click="loadUsersGrid">
                                <v-icon icon="mdi-magnify"></v-icon> Search
                            </v-btn>
                            <v-spacer></v-spacer>
                            <v-btn rounded="pill"
                                   color="primary"
                                   @click="clickAddNewUser">
                                <v-icon icon="mdi-plus"></v-icon> Add New User
                            </v-btn>
                            <v-dialog v-model="data.showEditingUserDialog"
                                      max-width="500px">
                                <v-card>
                                    <v-card-title>Edit User</v-card-title>

                                    <v-card-text>
                                        <v-container>
                                            <v-row>
                                                <v-col cols="12">
                                                    <v-text-field v-model="data.editUser.firstName"
                                                                  label="First Name"></v-text-field>
                                                </v-col>
                                                <v-col cols="12">
                                                    <v-text-field v-model="data.editUser.lastName"
                                                                  label="Last Name"></v-text-field>
                                                </v-col>
                                                <v-col cols="12">
                                                    <v-text-field v-model="data.editUser.email"
                                                                  label="Email"></v-text-field>
                                                </v-col>
                                                <v-col cols="12">
                                                    <v-select v-model="data.editUser.role"
                                                              :items="data.roles"
                                                              label="Role"></v-select>
                                                </v-col>
                                            </v-row>
                                        </v-container>
                                    </v-card-text>

                                    <v-card-actions>
                                        <v-spacer></v-spacer>
                                        <v-btn color="success"
                                               @click="editUserSave">
                                            Save
                                        </v-btn>
                                        <v-btn color="error"
                                               @click="editUserClose">
                                            Cancel
                                        </v-btn>
                                    </v-card-actions>
                                </v-card>
                            </v-dialog>
                        </v-toolbar>

                        <v-alert v-if="data.message" :type="data.messageType as any" density="compact" class="ma-2">
                            {{ data.message }}
                        </v-alert>
                    </template>

                    <template v-slot:item.networkTestCharacterCount="{ item }">
                        <div class="d-flex align-center">
                            <v-switch :model-value="netTestState(item.raw).all"
                                      color="info"
                                      density="compact"
                                      hide-details
                                      :disabled="data.savingUserGuid === item.raw.userGUID || netTestState(item.raw).total === 0"
                                      @update:modelValue="() => setNetTest(item.raw, !netTestState(item.raw).all)"></v-switch>
                            <span v-if="netTestState(item.raw).partial"
                                  class="text-caption"
                                  style="margin-left:6px; white-space:nowrap;"
                                  :title="'Set on some characters only'">
                                {{ netTestState(item.raw).flagged }}/{{ netTestState(item.raw).total }}
                            </span>
                            <span v-else-if="netTestState(item.raw).total === 0"
                                  class="text-caption text-disabled"
                                  style="margin-left:6px; white-space:nowrap;">
                                no chars
                            </span>
                        </div>
                    </template>

                    <template v-slot:no-data>
                        <div class="pa-4">No users matched.</div>
                    </template>

                    <template v-slot:item.actions="{ item }">
                        <v-icon size="small"
                                class="me-2"
                                title="Edit user"
                                @click="editUser(item.raw)"
                                style="margin-right:10px;">
                            mdi-pencil
                        </v-icon>
                        <v-icon size="small"
                                class="me-2"
                                title="Characters"
                                @click="viewCharacters(item.raw)"
                                style="margin-right:10px;">
                            mdi-account-group
                        </v-icon>
                        <v-icon size="small"
                                title="Delete user"
                                @click="deleteUser(item.raw)">
                            mdi-delete
                        </v-icon>
                    </template>
                </v-data-table>
            </div>

            <v-alert type="info" density="compact" style="margin-top: 24px;">
                Results are capped at 200 rows - narrow the search rather than paging if you do
                not see someone. Steam accounts store the persona name in First Name and get a
                synthetic <code>steam_&lt;id&gt;@steam.samsarasaga.invalid</code> email, so searching
                the Steam ID finds them either way.
            </v-alert>

            <v-alert type="warning" density="compact" style="margin-top: 12px;">
                <strong>Net Test</strong> sets <code>Characters.IsInternalNetworkTestUser</code> on
                <em>every</em> character the account owns, so the server hands them
                <code>127.0.0.1</code> instead of the zone server's real IP. Only useful when that
                player is on the same machine as the zone server - for anyone else it makes the
                game unjoinable until switched back off. A <code>2/4</code> badge means it was set
                per character on the Characters page; switching on here brings the rest into line.
            </v-alert>

            <v-alert type="info" density="compact" style="margin-top: 12px;">
                Role is stored on <code>Users.Role</code>. Nothing in the API checks it yet, so
                it records intent rather than granting access. In-game admin comes from the
                per-character flags on the Characters page.
            </v-alert>
        </div>
    </div>
</v-container>
</template>

<style scoped>
    .users-container {
        margin-top: 0px;
    }
</style>

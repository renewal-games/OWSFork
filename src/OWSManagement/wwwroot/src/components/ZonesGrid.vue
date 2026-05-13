<script setup lang="ts">
    import { reactive } from 'vue';
    import owsApi from '../owsApi';

    interface Data {
        headers: Array<object>,
        rows: Array<Record<string, unknown>>,
        showAddZoneDialog: boolean,
        isSavingZone: boolean,
        addZoneForm: Record<string, unknown>
    }

    function defaultAddZoneForm(): Record<string, unknown> {
        return {
            zoneName: '',
            mapName: '',
            worldCompContainsFilter: '',
            worldCompListFilter: '',
            softPlayerCap: 0,
            hardPlayerCap: 0,
            mapMode: 1,
            minutesToShutdownAfterEmpty: 5
        };
    }

    const data: Data = reactive({
        headers: [
            { title: 'Map ID', key: 'mapID' },
            { title: 'Map', key: 'mapName' },
            { title: 'Zone', key: 'zoneName' },
            { title: 'Soft Cap', key: 'softPlayerCap' },
            { title: 'Hard Cap', key: 'hardPlayerCap' },
            { title: 'Mode', key: 'mapMode' },
            { title: 'Shutdown After Empty', key: 'minutesToShutdownAfterEmpty' },
        ],
        rows: [
        ],
        showAddZoneDialog: false,
        isSavingZone: false,
        addZoneForm: defaultAddZoneForm()
    });

    function clickAddNewZone() {
        data.addZoneForm = defaultAddZoneForm();
        data.showAddZoneDialog = true;
    }

    function addZoneClose() {
        data.showAddZoneDialog = false;
    }

    function addZoneSave() {
        data.isSavingZone = true;

        owsApi.addZone({
            addOrUpdateZone: data.addZoneForm
        }).then((response: any) => {
            const success = response.data?.success === true || response.data?.Success === true;

            if (success) {
                data.rows.push(Object.assign({ mapID: '' }, data.addZoneForm));
                data.showAddZoneDialog = false;
            }
            else {
                alert(response.data?.errorMessage ?? response.data?.ErrorMessage ?? "Unable to add the zone!");
            }
        }).catch((error: any) => {
            console.log(error);
        }).finally(function () {
            data.isSavingZone = false;
        });
    }
</script>

<template>
<v-container>
    <div class="zones-container">
        <div>
            <v-data-table :headers="data.headers"
                          :items="data.rows"
                          :items-per-page="5"
                          class="elevation-1 users-table">
                <template v-slot:top>
                    <v-toolbar flat>
                        <v-toolbar-title>Zones</v-toolbar-title>
                        <v-divider class="mx-4"
                                   inset
                                   vertical></v-divider>
                        <v-spacer></v-spacer>
                        <v-btn rounded="pill"
                               color="primary"
                               @click="clickAddNewZone">
                            <v-icon icon="mdi-plus"></v-icon> Add Zone
                        </v-btn>
                        <v-dialog v-model="data.showAddZoneDialog"
                                  max-width="720px">
                            <v-card>
                                <v-card-title>Add Zone</v-card-title>

                                <v-card-text>
                                    <v-container>
                                        <v-row>
                                            <v-col cols="12"
                                                   sm="6">
                                                <v-text-field v-model="data.addZoneForm.zoneName"
                                                              label="Zone Name"
                                                              required></v-text-field>
                                            </v-col>
                                            <v-col cols="12"
                                                   sm="6">
                                                <v-text-field v-model="data.addZoneForm.mapName"
                                                              label="Map Name"
                                                              required></v-text-field>
                                            </v-col>
                                            <v-col cols="12"
                                                   sm="6">
                                                <v-text-field v-model.number="data.addZoneForm.softPlayerCap"
                                                              label="Soft Player Cap"
                                                              type="number"></v-text-field>
                                            </v-col>
                                            <v-col cols="12"
                                                   sm="6">
                                                <v-text-field v-model.number="data.addZoneForm.hardPlayerCap"
                                                              label="Hard Player Cap"
                                                              type="number"></v-text-field>
                                            </v-col>
                                            <v-col cols="12"
                                                   sm="6">
                                                <v-text-field v-model.number="data.addZoneForm.mapMode"
                                                              label="Map Mode"
                                                              type="number"></v-text-field>
                                            </v-col>
                                            <v-col cols="12"
                                                   sm="6">
                                                <v-text-field v-model.number="data.addZoneForm.minutesToShutdownAfterEmpty"
                                                              label="Minutes To Shutdown After Empty"
                                                              min="0"
                                                              type="number"></v-text-field>
                                            </v-col>
                                            <v-col cols="12"
                                                   sm="6">
                                                <v-text-field v-model="data.addZoneForm.worldCompContainsFilter"
                                                              label="World Comp Contains Filter"></v-text-field>
                                            </v-col>
                                            <v-col cols="12"
                                                   sm="6">
                                                <v-text-field v-model="data.addZoneForm.worldCompListFilter"
                                                              label="World Comp List Filter"></v-text-field>
                                            </v-col>
                                        </v-row>
                                    </v-container>
                                </v-card-text>

                                <v-card-actions>
                                    <v-spacer></v-spacer>
                                    <v-btn color="success"
                                           :loading="data.isSavingZone"
                                           @click="addZoneSave">
                                        Save
                                    </v-btn>
                                    <v-btn color="error"
                                           @click="addZoneClose">
                                        Cancel
                                    </v-btn>
                                </v-card-actions>
                            </v-card>
                        </v-dialog>
                    </v-toolbar>
                </template>
            </v-data-table>
        </div>
    </div>
</v-container>
</template>

<style scoped>
    .zones-container {
        margin-top: 0px;
        text-align: center;
    }
</style>

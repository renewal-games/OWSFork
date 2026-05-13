<script setup lang="ts">
    import { reactive } from 'vue';

    interface Data {
        headers: Object,
        rows: Object
    }

    const data: Data = reactive({
        headers: [
            { title: 'Instance ID', key: 'mapInstanceID' },
            { title: 'World Server ID', key: 'worldServerID' },
            { title: 'Zone', key: 'zoneName' },
            { title: 'Port', key: 'port' },
            { title: 'Status', key: 'status' },
            { title: 'Players', key: 'numberOfReportedPlayers' },
            { title: 'Last Empty', key: 'lastServerEmptyDate' },
            { title: 'Shutdown After Empty', key: 'minutesToShutdownAfterEmpty' },
        ],
        rows: [
        ]
    });
</script>

<template>
<v-container>
    <div class="zone-instances-container">
        <h2>Zone Instances</h2>
        <div>
            <v-data-table :headers="data.headers"
                          :items="data.rows"
                          :items-per-page="5"
                          class="elevation-1 users-table">
            </v-data-table>
        </div>
    </div>
</v-container>
</template>

<style scoped>
    .zone-instances-container {
        margin-top: 0px;
        text-align: center;
    }
</style>

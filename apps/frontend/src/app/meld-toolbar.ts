import { Component, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatToolbarModule } from '@angular/material/toolbar';

export type LocalPage = 'home' | 'about-me' | 'account';

@Component({
  selector: 'app-meld-toolbar',
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatToolbarModule],
  templateUrl: './meld-toolbar.html',
  styleUrl: './meld-toolbar.css',
})
export class MeldToolbar {
  readonly page = input<string>(location.pathname);
  readonly accountName = input<string | null>(null);
  readonly dummy = input(false);
  readonly updateCount = input(0);
}

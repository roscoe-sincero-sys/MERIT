import { inject, Injectable } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';

@Injectable()
export class PageTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);
  override updateTitle(snapshot: RouterStateSnapshot): void {
    const page = this.buildTitle(snapshot)?.replace(/ - MERIT$/, '');
    this.title.setTitle(page ? `${page} - MERIT` : 'MERIT');
  }
}
